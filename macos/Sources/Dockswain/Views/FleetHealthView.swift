import SwiftUI

struct FleetHealthView: View {
    @EnvironmentObject var state: AppState
    let onOpen: (String) -> Void

    private enum Section: String, CaseIterable { case problems = "Problems", hosts = "Hosts", ssl = "SSL", images = "Images", history = "History" }
    @State private var section: Section = .problems

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Image(systemName: "heart.text.square.fill").foregroundStyle(.tint)
                VStack(alignment: .leading, spacing: 1) {
                    Text("Fleet Health").font(.headline)
                    Text("All configured Docker hosts").font(.caption2).foregroundStyle(.secondary)
                }
                Spacer()
                if state.fleet.isRefreshing { ProgressView().controlSize(.small) }
                Button { onOpen("containers") } label: { Label("Containers", systemImage: "shippingbox") }
                    .buttonStyle(.borderless)
                Button { state.fleet.refreshNow() } label: { Image(systemName: "arrow.clockwise") }
                    .buttonStyle(.borderless).help("Refresh every host")
                Button { onOpen("settings") } label: { Image(systemName: "gearshape") }
                    .buttonStyle(.borderless).help("Settings")
            }.padding(10)
            Divider()

            HStack(spacing: 7) {
                metric("Hosts", "\(state.fleet.onlineCount)/\(state.servers.count)", .accentColor)
                metric("Healthy", "\(state.fleet.healthyCount)", .green)
                metric("Warnings", "\(state.fleet.warningCount)", .orange)
                metric("Critical", "\(state.fleet.criticalCount)", .red)
            }.padding(8)

            Picker("", selection: $section) {
                ForEach(Section.allCases, id: \.self) { item in
                    Text(title(item)).tag(item)
                }
            }.pickerStyle(.segmented).labelsHidden().padding(.horizontal, 8).padding(.bottom, 8)
            Divider()

            Group {
                switch section {
                case .problems: problems
                case .hosts: hosts
                case .ssl: certificates
                case .images: images
                case .history: history
                }
            }
        }
    }

    private func title(_ section: Section) -> String {
        switch section {
        case .problems: return "Problems \(state.fleet.issues.count)"
        case .ssl: return "SSL \(state.fleet.expiringCertificates.filter { $0.2 <= state.fleetSslDays }.count)"
        case .images: return "Images \(state.fleet.imageUpdateCount)"
        default: return section.rawValue
        }
    }

    private func metric(_ label: String, _ value: String, _ color: Color) -> some View {
        VStack(spacing: 2) {
            Text(value).font(.system(size: 18, weight: .bold, design: .rounded)).foregroundStyle(color)
            Text(label).font(.caption2).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity).padding(.vertical, 7)
        .background(RoundedRectangle(cornerRadius: 7).fill(color.opacity(0.10)))
        .overlay(RoundedRectangle(cornerRadius: 7).stroke(color.opacity(0.25)))
    }

    @ViewBuilder private var problems: some View {
        if state.servers.isEmpty { empty("No servers configured", "Add Docker hosts in Settings.", "server.rack") }
        else if state.fleet.issues.isEmpty { empty("Fleet looks healthy", "No current operational problems were detected.", "checkmark.circle") }
        else {
            ScrollView {
                LazyVStack(spacing: 6) {
                    ForEach(state.fleet.issues) { issue in issueRow(issue) }
                }.padding(8)
            }
        }
    }

    private func issueRow(_ issue: FleetIssue) -> some View {
        HStack(spacing: 8) {
            Circle().fill(color(issue.severity)).frame(width: 8, height: 8)
            VStack(alignment: .leading, spacing: 2) {
                Text(issue.title).font(.system(size: 12, weight: .semibold)).lineLimit(1)
                Text(issue.serverLabel + (issue.containerName.isEmpty ? "" : " / \(issue.containerName)"))
                    .font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                if !issue.detail.isEmpty { Text(issue.detail).font(.caption2).foregroundStyle(.tertiary).lineLimit(1) }
            }
            Spacer()
            if !issue.containerID.isEmpty {
                Button("Restart") { restart(issue) }.buttonStyle(.borderless)
            }
            Button(issue.feature == "disk" ? "Cleanup" : issue.feature == "nginx" ? "Certificates" : "Open") {
                open(issue.serverID, feature: issue.feature)
            }.buttonStyle(.borderless)
        }
        .padding(8)
        .background(RoundedRectangle(cornerRadius: 7).fill(color(issue.severity).opacity(0.08)))
        .overlay(RoundedRectangle(cornerRadius: 7).stroke(color(issue.severity).opacity(0.25)))
    }

    private var hosts: some View {
        ScrollView {
            LazyVStack(spacing: 6) {
                ForEach(state.servers) { server in
                    let snapshot = state.fleet.snapshots[server.id]
                    HStack(spacing: 8) {
                        Circle().fill(snapshot == nil || snapshot?.reachable == false ? .red : snapshot?.dockerOK == false ? .orange : .green)
                            .frame(width: 8, height: 8)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(server.label.isEmpty ? server.target : server.label).font(.system(size: 12, weight: .semibold))
                            Text(hostDetail(snapshot)).font(.caption2).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("Open") { open(server.id, feature: "containers") }.buttonStyle(.borderless)
                    }.padding(8).background(RoundedRectangle(cornerRadius: 7).fill(Color.primary.opacity(0.05)))
                }
            }.padding(8)
        }
    }

    private func hostDetail(_ host: FleetHostSnapshot?) -> String {
        guard let host else { return "Waiting for first sample" }
        if !host.reachable { return "Offline · \(host.reason)" }
        if !host.dockerOK { return "SSH online · Docker unavailable (\(host.reason))" }
        let running = host.containers.filter(\.isRunning).count
        var parts = ["\(running)/\(host.containers.count) running"]
        if let memory = host.resources?.memoryPct { parts.append("memory \(Int(memory))%") }
        if let disk = host.disk { parts.append("disk \(disk.usePct)") }
        return parts.joined(separator: " · ")
    }

    @ViewBuilder private var certificates: some View {
        if state.fleet.expiringCertificates.isEmpty { empty("No certificates reported", "Certbot may not be installed, or the first SSL check is running.", "lock.shield") }
        else {
            ScrollView {
                LazyVStack(spacing: 6) {
                    ForEach(certificateRows) { row in
                        HStack {
                            Image(systemName: row.days < 0 ? "xmark.shield.fill" : row.days <= state.fleetSslDays ? "exclamationmark.shield.fill" : "checkmark.shield.fill")
                                .foregroundStyle(row.days < 0 ? .red : row.days <= state.fleetSslDays ? .orange : .green)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(row.cert.domains.isEmpty ? row.cert.name : row.cert.domains).font(.system(size: 12, design: .monospaced)).lineLimit(1)
                                Text("\(row.host.label) · \(row.cert.expiry)").font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                            }
                            Spacer(); Text(row.days < 0 ? "expired \(-row.days)d" : "\(row.days)d").font(.caption.bold())
                            Button("Manage") { open(row.host.id, feature: "nginx") }.buttonStyle(.borderless)
                        }.padding(8).background(RoundedRectangle(cornerRadius: 7).fill(Color.primary.opacity(0.05)))
                    }
                }.padding(8)
            }
        }
    }

    private struct CertificateRow: Identifiable {
        let host: FleetHostSnapshot
        let cert: Cert
        let days: Int
        var id: String { "\(host.id):\(cert.id)" }
    }
    private var certificateRows: [CertificateRow] {
        state.fleet.expiringCertificates.map { CertificateRow(host: $0.0, cert: $0.1, days: $0.2) }
    }

    private var images: some View {
        VStack(spacing: 0) {
            Text("Read-only local tag comparison; Dockswain never pulls images in the background.")
                .font(.caption2).foregroundStyle(.secondary).padding(8)
            Divider()
            ScrollView {
                LazyVStack(spacing: 6) {
                    ForEach(imageRows, id: \.id) { row in
                        HStack {
                            Image(systemName: row.update ? "arrow.down.circle.fill" : row.pinned ? "lock.fill" : "shippingbox")
                                .foregroundStyle(row.update ? .orange : .secondary)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(row.ref).font(.system(size: 12, design: .monospaced)).lineLimit(1)
                                Text("\(row.host) · \(row.update ? "newer local image available" : row.pinned ? "digest pinned" : "tag reference")")
                                    .font(.caption2).foregroundStyle(.secondary)
                            }
                            Spacer(); Button("Open") { open(row.serverID, feature: "containers") }.buttonStyle(.borderless)
                        }.padding(8).background(RoundedRectangle(cornerRadius: 7).fill(Color.primary.opacity(0.05)))
                    }
                }.padding(8)
            }
        }
    }

    private struct ImageRow: Identifiable { let id: String; let serverID: UUID; let host: String; let ref: String; let pinned: Bool; let update: Bool }
    private var imageRows: [ImageRow] {
        state.fleet.snapshots.values.flatMap { host in
            var seen: Set<String> = []
            return host.containers.compactMap { c in
                let ref = c.imageRef ?? c.image ?? ""
                guard !ref.isEmpty, seen.insert(ref).inserted else { return nil }
                return ImageRow(id: "\(host.id):\(ref)", serverID: host.id, host: host.label, ref: ref,
                                pinned: c.imagePinned == true, update: c.imageUpdate == true)
            }
        }.sorted {
            if $0.update != $1.update { return $0.update }
            return $0.ref < $1.ref
        }
    }

    private var history: some View {
        VStack(spacing: 0) {
            HStack { Text("Persistent event history · newest first").font(.caption2).foregroundStyle(.secondary); Spacer(); Button("Clear") { state.fleet.clearEvents() }.buttonStyle(.borderless).disabled(state.fleet.events.isEmpty) }.padding(8)
            Divider()
            if state.fleet.events.isEmpty { empty("No changes recorded yet", "The first poll establishes a silent baseline.", "clock.arrow.circlepath") }
            else {
                ScrollView {
                    LazyVStack(spacing: 5) {
                        ForEach(state.fleet.events) { event in
                            HStack(spacing: 7) {
                                Text(event.timestamp, style: .time).font(.caption2.monospacedDigit()).foregroundStyle(.secondary).frame(width: 48)
                                Circle().fill(color(event.severity)).frame(width: 7, height: 7)
                                VStack(alignment: .leading, spacing: 1) {
                                    Text(event.title).font(.system(size: 11)).lineLimit(1)
                                    Text(event.serverLabel + (event.containerName.isEmpty ? "" : " / \(event.containerName)") + (event.detail.isEmpty ? "" : " · \(event.detail)"))
                                        .font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                                }
                                Spacer(); Button { open(event.serverID, feature: "containers") } label: { Image(systemName: "chevron.right") }.buttonStyle(.borderless)
                            }.padding(.horizontal, 8).padding(.vertical, 4)
                        }
                    }
                }
            }
        }
    }

    private func empty(_ title: String, _ detail: String, _ symbol: String) -> some View {
        VStack(spacing: 7) { Spacer(); Image(systemName: symbol).font(.system(size: 30)).foregroundStyle(.secondary); Text(title).font(.headline); Text(detail).font(.caption).foregroundStyle(.secondary); Spacer() }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
    private func color(_ severity: FleetSeverity) -> Color { severity == .critical ? .red : severity == .warning ? .orange : severity == .ok ? .green : .secondary }
    private func open(_ serverID: UUID, feature: String) { if let server = state.servers.first(where: { $0.id == serverID }) { state.open(server); onOpen(feature.isEmpty ? "containers" : feature) } }
    private func restart(_ issue: FleetIssue) {
        guard !issue.containerID.isEmpty, let server = state.servers.first(where: { $0.id == issue.serverID }) else { return }
        Task { try? await state.makeBackend().action("restart", container: issue.containerID, on: server); state.fleet.refreshNow() }
    }
}
