import Foundation

enum FleetSeverity: String, Codable { case info, ok, warning, critical }

struct FleetContainerSnapshot: Codable, Identifiable, Equatable {
    let id: String?
    let fullId: String?
    let name: String?
    let image: String?
    let state: String?
    let status: String?
    let health: String?
    let restartCount: Int?
    let exitCode: Int?
    let startedAt: String?
    let finishedAt: String?
    let imageRef: String?
    let imageId: String?
    let currentImageId: String?
    let imageCreated: String?
    let imageUpdate: Bool?
    let imagePinned: Bool?
    let cpu: String?
    let mem: String?
    let memUsage: String?

    var stableID: String { fullId ?? id ?? cleanName }
    var shortID: String { String((id ?? stableID).prefix(12)) }
    var cleanName: String { (name ?? "").trimmingCharacters(in: CharacterSet(charactersIn: "/")) }
    var stateValue: String { (state ?? "").lowercased() }
    var healthValue: String {
        if let health, !health.isEmpty { return health.lowercased() }
        let value = (status ?? "").lowercased()
        if value.contains("unhealthy") { return "unhealthy" }
        if value.contains("health: starting") { return "starting" }
        if value.contains("healthy") { return "healthy" }
        return ""
    }
    var isRunning: Bool { stateValue == "running" }
    var isLive: Bool { ["running", "restarting", "paused"].contains(stateValue) }
    var cpuPercent: Double { Self.percent(cpu) }
    var memoryPercent: Double { Self.percent(mem) }

    private static func percent(_ value: String?) -> Double {
        Double((value ?? "").replacingOccurrences(of: "%", with: "")) ?? 0
    }
}

struct FleetHostResources: Codable, Equatable {
    let cpus: Int?
    let load1: Double?
    let memoryTotal: Double?
    let memoryUsed: Double?
    let memoryPct: Double?
}

struct FleetImageSnapshot: Codable, Equatable {
    let ref: String?
    let id: String?
    let created: String?
    let tags: [String]?
    let digests: [String]?
}

struct FleetHealthResponse: Decodable {
    let ok: Bool
    let reachable: Bool?
    let dockerOk: Bool?
    let reason: String?
    let version: String?
    let containers: [FleetContainerSnapshot]?
    let host: FleetHostResources?
    let images: [FleetImageSnapshot]?
}

struct FleetHostSnapshot: Codable, Identifiable, Equatable {
    let id: UUID
    var label: String
    var sampledAt: Date
    var metaSampledAt: Date? = nil
    var reachable: Bool
    var dockerOK: Bool
    var reason: String
    var version: String
    var containers: [FleetContainerSnapshot]
    var resources: FleetHostResources?
    var disk: DiskInfo?
    var dockerDisk: [DfEntry]
    var certificates: [Cert]
}

struct FleetEvent: Codable, Identifiable, Equatable {
    let id: UUID
    let timestamp: Date
    let kind: String
    let severity: FleetSeverity
    let serverID: UUID
    let serverLabel: String
    let containerID: String
    let containerName: String
    let title: String
    let detail: String
    let count: Int
}

struct FleetIssue: Identifiable {
    let id: String
    let severity: FleetSeverity
    let kind: String
    let serverID: UUID
    let serverLabel: String
    let containerID: String
    let containerName: String
    let title: String
    let detail: String
    let feature: String
}

struct FleetSettings: Equatable {
    var refreshInterval: Double = 30
    var deepInterval: Double = 900
    var resources = true
    var disk = true
    var ssl = true
    var images = true
    var notifications = true
    var notifyRecovery = false
    var cpuThreshold = 85
    var memoryThreshold = 85
    var diskThreshold = 85
    var sslDays = 14
    var restartThreshold = 3
    var restartWindowMinutes = 60
    var historyLimit = 250
}

extension Backend {
    func fleetHealth(_ server: Server, resources: Bool) async throws -> FleetHealthResponse {
        let out = try await runRaw(["fleet-health"] + sshArgs(server) + [resources ? "1" : "0"], env: env(server))
        guard let line = out.split(whereSeparator: \.isNewline).last(where: { $0.hasPrefix("{") }),
              let data = line.data(using: .utf8) else { throw BackendError.decode(out) }
        do { return try JSONDecoder().decode(FleetHealthResponse.self, from: data) }
        catch { throw BackendError.decode(String(line)) }
    }
}
