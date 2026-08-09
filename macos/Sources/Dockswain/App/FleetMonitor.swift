import Foundation
import Combine

/// Polls every configured server independently of the open management tabs,
/// persists a bounded transition history, and derives the current problem list.
@MainActor
final class FleetMonitor: ObservableObject {
    @Published private(set) var snapshots: [UUID: FleetHostSnapshot] = [:]
    @Published private(set) var events: [FleetEvent] = []
    @Published private(set) var isRefreshing = false

    private var servers: [Server] = []
    private var settings = FleetSettings()
    private var backendFactory: (() -> Backend)?
    private var pollTask: Task<Void, Never>?
    private var lastDeep = Date.distantPast
    private let snapshotsKey = "fleet.snapshots.v1"
    private let eventsKey = "fleet.events.v1"

    init() {
        if let data = UserDefaults.standard.data(forKey: snapshotsKey),
           let decoded = try? JSONDecoder().decode([UUID: FleetHostSnapshot].self, from: data) { snapshots = decoded }
        if let data = UserDefaults.standard.data(forKey: eventsKey),
           let decoded = try? JSONDecoder().decode([FleetEvent].self, from: data) { events = decoded }
    }

    deinit { pollTask?.cancel() }

    func configure(servers: [Server], settings: FleetSettings, backend: @escaping () -> Backend) {
        let needsRestart = self.servers.map(\.id) != servers.map(\.id)
            || self.settings.refreshInterval != settings.refreshInterval
        self.servers = servers
        self.settings = settings
        self.backendFactory = backend
        let valid = Set(servers.map(\.id))
        let snapshotCount = snapshots.count
        snapshots = snapshots.filter { valid.contains($0.key) }
        if snapshots.count != snapshotCount { persistSnapshots() }
        if needsRestart || pollTask == nil { restart() }
        objectWillChange.send()
    }

    func restart() {
        pollTask?.cancel()
        guard !servers.isEmpty else { pollTask = nil; return }
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                await self?.refreshAll()
                let seconds = max(10, self?.settings.refreshInterval ?? 30)
                try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
            }
        }
    }

    func refreshNow() { Task { await refreshAll(forceDeep: true) } }

    func refreshAll(forceDeep: Bool = false) async {
        guard !isRefreshing, let backendFactory else { return }
        isRefreshing = true
        defer { isRefreshing = false }
        let deep = forceDeep || Date().timeIntervalSince(lastDeep) >= max(300, settings.deepInterval)
        let targets = servers
        let monitorSettings = settings

        await withTaskGroup(of: Sample.self) { group in
            for server in targets {
                group.addTask {
                    var health: FleetHealthResponse?
                    var healthError = ""
                    do { health = try await backendFactory().fleetHealth(server, resources: monitorSettings.resources) }
                    catch { healthError = error.localizedDescription }
                    var disk: DiskInfo?; var df: [DfEntry] = []; var certs: [Cert] = []
                    if deep, health?.reachable != false {
                        if monitorSettings.disk,
                           let value = try? await backendFactory().disk(server) { disk = value.0; df = value.1 }
                        if monitorSettings.ssl { certs = (try? await backendFactory().certbotList(server)) ?? [] }
                    }
                    return Sample(server: server, health: health, healthError: healthError,
                                  disk: disk, df: df, certs: certs, deep: deep)
                }
            }
            for await sample in group { apply(sample) }
        }
        if deep { lastDeep = Date() }
        persistSnapshots()
    }

    private struct Sample {
        let server: Server
        let health: FleetHealthResponse?
        let healthError: String
        let disk: DiskInfo?
        let df: [DfEntry]
        let certs: [Cert]
        let deep: Bool
    }

    private func apply(_ sample: Sample) {
        let server = sample.server
        let old = snapshots[server.id]
        let response = sample.health
        let label = server.label.isEmpty ? server.target : server.label
        var next = FleetHostSnapshot(
            id: server.id, label: label, sampledAt: Date(),
            metaSampledAt: old?.metaSampledAt,
            reachable: response?.reachable ?? false,
            dockerOK: response?.dockerOk ?? false,
            reason: response?.reason ?? (sample.healthError.isEmpty ? "parse_error" : sample.healthError),
            version: response?.version ?? old?.version ?? "",
            containers: response?.ok == true ? (response?.containers ?? []) : [],
            resources: response?.host,
            disk: old?.disk, dockerDisk: old?.dockerDisk ?? [], certificates: old?.certificates ?? [])
        let metaObserved = sample.deep && response?.reachable == true
        if metaObserved {
            if let disk = sample.disk { next.disk = disk; next.dockerDisk = sample.df }
            next.certificates = sample.certs
            next.metaSampledAt = Date()
        }
        if let old { detectTransitions(old: old, next: next, server: server, deep: metaObserved) }
        snapshots[server.id] = next
    }

    private func detectTransitions(old: FleetHostSnapshot, next: FleetHostSnapshot,
                                   server: Server, deep: Bool) {
        if old.reachable && !next.reachable {
            add("host_offline", .critical, server, nil, "\(next.label) is offline", next.reason)
        } else if !old.reachable && next.reachable {
            add("host_online", .ok, server, nil, "\(next.label) is back online", "SSH connection recovered")
        }
        if old.reachable && old.dockerOK && next.reachable && !next.dockerOK {
            add("docker_unavailable", .critical, server, nil, "Docker unavailable on \(next.label)", next.reason)
        } else if old.reachable && !old.dockerOK && next.dockerOK {
            add("docker_recovered", .ok, server, nil, "Docker recovered on \(next.label)", "Docker is responding again")
        }
        if deep, settings.disk, let beforeDisk = old.disk, let nowDisk = next.disk {
            threshold(percent(beforeDisk.usePct), percent(nowDisk.usePct), Double(settings.diskThreshold),
                      "disk", "Disk", server, nil)
        }
        if deep, settings.ssl {
            let oldCerts = Dictionary(uniqueKeysWithValues: old.certificates.map { ($0.id, $0) })
            let priorSample = old.metaSampledAt ?? old.sampledAt
            for cert in next.certificates {
                guard let beforeCert = oldCerts[cert.id] else { continue }
                let beforeDays = daysRemaining(beforeCert.expiry, relativeTo: priorSample)
                let nowDays = daysRemaining(cert.expiry)
                if beforeDays > settings.sslDays, nowDays <= settings.sslDays {
                    let detail = nowDays < 0 ? "Expired \(-nowDays) days ago" : "\(nowDays) days remaining"
                    add("ssl_expiring", nowDays < 0 ? .critical : .warning, server, nil,
                        "Certificate expiring: \(cert.domains.isEmpty ? cert.name : cert.domains)", detail)
                } else if beforeDays <= settings.sslDays, nowDays > settings.sslDays {
                    add("ssl_recovered", .ok, server, nil,
                        "Certificate renewed: \(cert.domains.isEmpty ? cert.name : cert.domains)", "\(nowDays) days remaining")
                }
            }
        }
        guard old.dockerOK, next.dockerOK else { return }
        let before = Dictionary(uniqueKeysWithValues: old.containers.map { ($0.stableID, $0) })
        let current = Dictionary(uniqueKeysWithValues: next.containers.map { ($0.stableID, $0) })
        for (id, now) in current {
            guard let prior = before[id] else {
                add("container_created", .info, server, now, "\(now.cleanName) was created", now.imageRef ?? now.image ?? "")
                continue
            }
            let delta = max(0, (now.restartCount ?? 0) - (prior.restartCount ?? 0))
            if delta > 0 { add("container_restart", .warning, server, now, "\(now.cleanName) restarted", "\(delta) new restart\(delta == 1 ? "" : "s")", count: delta) }
            if now.isLive, prior.healthValue != "unhealthy", now.healthValue == "unhealthy" {
                add("container_unhealthy", .critical, server, now, "\(now.cleanName) became unhealthy", now.status ?? "")
            } else if prior.healthValue == "unhealthy", now.healthValue != "unhealthy", now.isRunning {
                add("container_recovered", .ok, server, now, "\(now.cleanName) recovered", now.status ?? "")
            }
            if prior.stateValue != "restarting", now.stateValue == "restarting" {
                add("container_restarting", .warning, server, now, "\(now.cleanName) is restarting", now.status ?? "")
            }
            if prior.isLive && !now.isLive {
                let crashed = (now.exitCode ?? 0) != 0
                add(crashed ? "container_crashed" : "container_stopped", crashed ? .critical : .warning,
                    server, now, crashed ? "\(now.cleanName) crashed" : "\(now.cleanName) stopped", now.status ?? "")
            } else if !prior.isLive && now.isLive {
                add("container_started", .info, server, now, "\(now.cleanName) started", now.status ?? "")
            }
            if settings.resources {
                if prior.cpu?.isEmpty == false, now.cpu?.isEmpty == false {
                    threshold(prior.cpuPercent, now.cpuPercent, Double(settings.cpuThreshold), "container_cpu", "CPU", server, now)
                }
                if prior.mem?.isEmpty == false, now.mem?.isEmpty == false {
                    threshold(prior.memoryPercent, now.memoryPercent, Double(settings.memoryThreshold), "container_memory", "memory", server, now)
                }
            }
            if settings.images, now.isLive, now.imagePinned != true,
               prior.imageUpdate != true, now.imageUpdate == true {
                add("image_update", .warning, server, now, "Newer local image for \(now.cleanName)", now.imageRef ?? now.image ?? "")
            } else if settings.images, now.isLive, now.imagePinned != true,
                      prior.imageUpdate == true, now.imageUpdate != true {
                add("image_updated", .ok, server, now, "\(now.cleanName) now uses the current image", now.imageRef ?? now.image ?? "")
            }
        }
        for (id, prior) in before where current[id] == nil {
            add("container_removed", .info, server, prior, "\(prior.cleanName) was removed", prior.imageRef ?? prior.image ?? "")
        }
        if settings.resources, let oldMemory = old.resources?.memoryPct, let currentMemory = next.resources?.memoryPct {
            threshold(oldMemory, currentMemory, Double(settings.memoryThreshold),
                      "host_memory", "Host memory", server, nil)
        }
    }

    private func threshold(_ before: Double, _ now: Double, _ limit: Double, _ key: String,
                           _ label: String, _ server: Server, _ container: FleetContainerSnapshot?) {
        if before < limit, now >= limit {
            add("\(key)_high", .warning, server, container, "High \(label)\(container.map { ": \($0.cleanName)" } ?? "")", String(format: "%.1f%%", now))
        } else if before >= limit, now < limit {
            add("\(key)_recovered", .ok, server, container, "\(label) recovered\(container.map { ": \($0.cleanName)" } ?? "")", String(format: "%.1f%%", now))
        }
    }

    private func add(_ kind: String, _ severity: FleetSeverity, _ server: Server,
                     _ container: FleetContainerSnapshot?, _ title: String, _ detail: String, count: Int = 1) {
        let label = server.label.isEmpty ? server.target : server.label
        let event = FleetEvent(id: UUID(), timestamp: Date(), kind: kind, severity: severity,
            serverID: server.id, serverLabel: label, containerID: container?.shortID ?? "",
            containerName: container?.cleanName ?? "", title: title, detail: detail, count: count)
        events.insert(event, at: 0)
        events = Array(events.prefix(max(50, settings.historyLimit)))
        persistEvents()
        let recovery = kind.contains("recovered") || kind == "host_online"
        var restartAlert = true
        if kind == "container_restart", let container {
            let recent = recentRestarts(server.id, container.shortID)
            restartAlert = recent >= settings.restartThreshold && recent - count < settings.restartThreshold
        }
        if restartAlert && settings.notifications && severity != .info && notificationEnabled(for: kind)
            && (!recovery || settings.notifyRecovery) {
            HealthMonitor.shared.postFleet(title: title, detail: "\(label) · \(detail)", critical: severity == .critical)
        }
    }

    private func notificationEnabled(for kind: String) -> Bool {
        switch kind {
        case "container_stopped", "container_crashed": return UDefault.bool("notifyOnStop", true)
        case "container_unhealthy", "container_recovered": return UDefault.bool("notifyOnUnhealthy", true)
        case "container_restart", "container_restarting": return UDefault.bool("notifyOnRestart", true)
        default: return true
        }
    }

    var issues: [FleetIssue] {
        var result: [FleetIssue] = []
        for server in servers {
            let label = server.label.isEmpty ? server.target : server.label
            guard let host = snapshots[server.id] else {
                result.append(issue("checking", .warning, server, nil, "Checking \(label)", "Waiting for the first fleet sample")); continue
            }
            if !host.reachable { result.append(issue("host_offline", .critical, server, nil, "Host offline", host.reason)); continue }
            if !host.dockerOK { result.append(issue("docker_unavailable", .critical, server, nil, "Docker unavailable", host.reason)); continue }
            if settings.resources, (host.resources?.memoryPct ?? 0) >= Double(settings.memoryThreshold) {
                result.append(issue("host_memory", .warning, server, nil, "Host memory \(Int(host.resources?.memoryPct ?? 0))%", "Memory threshold exceeded"))
            }
            for c in host.containers {
                if (c.isLive && c.healthValue == "unhealthy") || c.stateValue == "dead" {
                    result.append(issue("container_unhealthy", .critical, server, c, "\(c.cleanName) is unhealthy", c.status ?? "", feature: "containers"))
                }
                let recent = recentRestarts(server.id, c.shortID)
                if recent >= settings.restartThreshold { result.append(issue("restart_burst", .warning, server, c, "\(c.cleanName) restarted \(recent) times", "within \(settings.restartWindowMinutes) minutes", feature: "containers")) }
                if c.stateValue == "restarting" { result.append(issue("container_restarting", .warning, server, c, "\(c.cleanName) is restarting", c.status ?? "", feature: "containers")) }
                if settings.resources, c.isLive, c.cpuPercent >= Double(settings.cpuThreshold) { result.append(issue("container_cpu", .warning, server, c, "\(c.cleanName) CPU \(c.cpu ?? "")", "100% equals one fully used CPU core", feature: "containers")) }
                if settings.resources, c.isLive, c.memoryPercent >= Double(settings.memoryThreshold) { result.append(issue("container_memory", .warning, server, c, "\(c.cleanName) memory \(c.mem ?? "")", c.memUsage ?? "", feature: "containers")) }
                if settings.images, c.isLive, c.imagePinned != true, c.imageUpdate == true { result.append(issue("image_update", .warning, server, c, "\(c.cleanName) uses an older image", c.imageRef ?? c.image ?? "", feature: "containers")) }
                if isRecentCrash(c) { result.append(issue("container_crashed", .warning, server, c, "\(c.cleanName) exited with code \(c.exitCode ?? 0)", c.status ?? "", feature: "containers")) }
            }
            if settings.disk, let disk = host.disk, percent(disk.usePct) >= Double(settings.diskThreshold) {
                result.append(issue("disk", percent(disk.usePct) >= 95 ? .critical : .warning, server, nil, "Disk \(disk.usePct)", "\(Bytes.human(reclaimable(host.dockerDisk))) reclaimable", feature: "disk"))
            }
            if settings.ssl {
                for cert in host.certificates where daysRemaining(cert.expiry) <= settings.sslDays {
                    let days = daysRemaining(cert.expiry)
                    result.append(issue("ssl", days < 0 ? .critical : .warning, server, nil,
                        days < 0 ? "SSL expired: \(cert.domains)" : "SSL expires in \(days)d: \(cert.domains)", cert.expiry, feature: "nginx"))
                }
            }
        }
        return result.sorted { $0.severity.sortOrder > $1.severity.sortOrder }
    }

    private func issue(_ kind: String, _ severity: FleetSeverity, _ server: Server,
                       _ container: FleetContainerSnapshot?, _ title: String, _ detail: String,
                       feature: String = "") -> FleetIssue {
        FleetIssue(id: "\(server.id):\(kind):\(container?.shortID ?? title)", severity: severity,
            kind: kind, serverID: server.id, serverLabel: server.label.isEmpty ? server.target : server.label,
            containerID: container?.shortID ?? "", containerName: container?.cleanName ?? "",
            title: title, detail: detail, feature: feature)
    }

    var onlineCount: Int { snapshots.values.filter(\.reachable).count }
    var healthyCount: Int { snapshots.values.flatMap(\.containers).filter { $0.isRunning && $0.healthValue != "unhealthy" }.count }
    var criticalCount: Int { issues.filter { $0.severity == .critical }.count }
    var warningCount: Int { issues.filter { $0.severity == .warning }.count }
    var imageUpdateCount: Int { snapshots.values.flatMap(\.containers).filter { $0.isLive && $0.imagePinned != true && $0.imageUpdate == true }.count }
    var expiringCertificates: [(FleetHostSnapshot, Cert, Int)] {
        snapshots.values.flatMap { host in host.certificates.map { (host, $0, daysRemaining($0.expiry)) } }
            .sorted { $0.2 < $1.2 }
    }

    func clearEvents() { events = []; persistEvents() }

    private func recentRestarts(_ serverID: UUID, _ containerID: String) -> Int {
        let cutoff = Date().addingTimeInterval(-Double(settings.restartWindowMinutes * 60))
        return events.filter { $0.serverID == serverID && $0.containerID == containerID && $0.kind == "container_restart" && $0.timestamp >= cutoff }.reduce(0) { $0 + $1.count }
    }
    private func percent(_ value: String) -> Double { Double(value.replacingOccurrences(of: "%", with: "")) ?? 0 }
    private func isRecentCrash(_ container: FleetContainerSnapshot) -> Bool {
        guard container.stateValue == "exited", (container.exitCode ?? 0) != 0,
              let raw = container.finishedAt, !raw.isEmpty else { return false }
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let finished = fractional.date(from: raw) ?? ISO8601DateFormatter().date(from: raw)
        guard let finished else { return false }
        let age = Date().timeIntervalSince(finished)
        return age >= 0 && age <= Double(max(5, settings.restartWindowMinutes) * 60)
    }
    private func reclaimable(_ rows: [DfEntry]) -> Int64 { rows.reduce(0) { $0 + parseBytes($1.reclaimable) } }
    private func parseBytes(_ text: String) -> Int64 {
        let clean = text.split(separator: " ").first.map(String.init) ?? text
        let pattern = #"([0-9]+(?:\.[0-9]+)?)\s*([KMGTPE]?i?B)?"#
        guard let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]),
              let match = regex.firstMatch(in: clean, range: NSRange(clean.startIndex..., in: clean)),
              let nr = Range(match.range(at: 1), in: clean), let number = Double(clean[nr]) else { return 0 }
        var power = 0
        if match.numberOfRanges > 2, let ur = Range(match.range(at: 2), in: clean), let first = clean[ur].uppercased().first {
            power = ["K":1,"M":2,"G":3,"T":4,"P":5,"E":6][String(first)] ?? 0
        }
        return Int64(number * pow(1024, Double(power)))
    }
    private func daysRemaining(_ value: String, relativeTo reference: Date = Date()) -> Int {
        var raw = value
        if let separator = raw.range(of: " ") { raw.replaceSubrange(separator, with: "T") }
        let formatter = ISO8601DateFormatter()
        guard let date = formatter.date(from: raw) ?? ISO8601DateFormatter().date(from: value) else {
            if let match = value.range(of: #"-?[0-9]+ day"#, options: .regularExpression),
               let n = Int(value[match].split(separator: " ")[0]) { return n }
            return Int.max
        }
        return Calendar.current.dateComponents([.day], from: reference, to: date).day ?? Int.max
    }
    private func persistSnapshots() {
        if let data = try? JSONEncoder().encode(snapshots) { UserDefaults.standard.set(data, forKey: snapshotsKey) }
    }
    private func persistEvents() {
        if let data = try? JSONEncoder().encode(events) { UserDefaults.standard.set(data, forKey: eventsKey) }
    }
}

private extension FleetSeverity {
    var sortOrder: Int { self == .critical ? 3 : self == .warning ? 2 : self == .ok ? 1 : 0 }
}
