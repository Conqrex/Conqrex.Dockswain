import QtQuick
import org.kde.plasma.plasmoid
import org.kde.plasma.plasma5support as Plasma5Support
import "../code/format.js" as Fmt

// Fleet-wide state, transition detector, bounded event store, and notification
// bridge. The interactive server-tab controller remains in main.qml.
Item {
    id: fleet

    property var mainRoot
    property var servers: []
    property var snapshots: parseObject(Plasmoid.configuration.fleetStateJson)
    property var events: parseArray(Plasmoid.configuration.fleetEventsJson)

    property int hostCount: servers.length
    property int observedCount: 0
    property int checkingCount: Math.max(0, hostCount - observedCount)
    property int onlineCount: 0
    property int healthyCount: 0
    property int warningCount: 0
    property int unhealthyCount: 0
    property int problemCount: 0
    property int criticalProblemCount: 0
    property int warningProblemCount: 0
    property int imageUpdateCount: 0
    property int unpinnedImageCount: 0
    property int expiringCertCount: 0
    property bool loading: false

    property alias hostModel: hostsModel
    property alias problemModel: problemsModel
    property alias eventModel: eventsModel
    property alias certificateModel: certificatesModel
    property alias imageModel: imagesModel

    ListModel { id: hostsModel }
    ListModel { id: problemsModel }
    ListModel { id: eventsModel }
    ListModel { id: certificatesModel }
    ListModel { id: imagesModel }
    Timer {
        id: snapshotPersistTimer
        interval: 750
        repeat: false
        onTriggered: Plasmoid.configuration.fleetStateJson = JSON.stringify(fleet.snapshots)
    }

    function parseObject(raw) {
        try { var v = JSON.parse(raw || "{}"); return v && typeof v === "object" ? v : {}; }
        catch (e) { return {}; }
    }
    function parseArray(raw) {
        try { var v = JSON.parse(raw || "[]"); return Array.isArray(v) ? v : []; }
        catch (e) { return []; }
    }
    function clone(v) { return JSON.parse(JSON.stringify(v || {})); }
    function serverKey(s) {
        if (!s) return "";
        return (s.user ? s.user + "@" : "") + (s.host || "") + ":" + (s.port || 22);
    }
    function serverLabel(s) { return s ? (s.label || serverKey(s)) : i18n("Unknown host"); }
    function containerName(c) {
        var n = c ? (c.name || "") : "";
        return n.charAt(0) === "/" ? n.substring(1) : n;
    }
    function healthOf(c) {
        if (!c) return "";
        var h = (c.health || "").toLowerCase();
        if (h) return h;
        var st = (c.status || "").toLowerCase();
        if (st.indexOf("unhealthy") >= 0) return "unhealthy";
        if (st.indexOf("health: starting") >= 0) return "starting";
        if (st.indexOf("healthy") >= 0) return "healthy";
        return "";
    }
    function reasonText(reason) {
        switch (reason || "") {
        case "no_password": return i18n("Password is not saved");
        case "ssh_auth": return i18n("SSH authentication failed");
        case "dns": return i18n("Host name could not be resolved");
        case "refused": return i18n("SSH connection was refused");
        case "timeout": return i18n("SSH connection timed out");
        case "unreachable": return i18n("Network is unreachable");
        case "docker_down": return i18n("Docker daemon is not running");
        case "docker_permission": return i18n("Docker socket permission denied");
        case "docker_missing": return i18n("Docker is not installed or not in PATH");
        case "sudo_password": return i18n("Docker needs passwordless sudo");
        case "parse_error": return i18n("Monitor returned an invalid response");
        case "ssh_error": return i18n("SSH connection failed");
        default: return reason ? ("" + reason).replace(/_/g, " ") : "";
        }
    }
    function byId(list) {
        var out = {};
        (list || []).forEach(function (c) { out[c.fullId || c.id] = c; });
        return out;
    }
    function crosses(value, threshold) { return Fmt.pctNumber(value) >= Number(threshold || 0); }
    function isRecoveryKind(kind) { return kind.indexOf("recovered") >= 0 || kind === "host_online"; }
    function enabledOrDefault(value) { return value === undefined || value === null ? true : !!value; }
    function notificationCategoryEnabled(server, kind) {
        if (!server || !enabledOrDefault(server.notifyEnabled)) return false;
        if (kind === "host_offline" || kind === "host_online"
                || kind === "docker_unavailable" || kind === "docker_recovered")
            return enabledOrDefault(server.notifyAvailability);
        if (kind === "container_cpu_high" || kind === "container_cpu_recovered")
            return enabledOrDefault(server.notifyCpu);
        if (kind === "container_memory_high" || kind === "container_memory_recovered"
                || kind === "host_memory_high" || kind === "host_memory_recovered")
            return enabledOrDefault(server.notifyMemory);
        if (kind === "container_restart") return enabledOrDefault(server.notifyRestarts);
        if (kind === "image_update" || kind === "image_updated")
            return enabledOrDefault(server.notifyImages);
        if (kind === "disk_pressure" || kind === "disk_recovered")
            return enabledOrDefault(server.notifyDisk);
        if (kind === "ssl_expiring" || kind === "ssl_recovered")
            return enabledOrDefault(server.notifySsl);
        if (kind === "container_unhealthy" || kind === "container_recovered"
                || kind === "container_restarting" || kind === "container_crashed"
                || kind === "container_stopped" || kind === "container_started")
            return enabledOrDefault(server.notifyHealth);
        return true;
    }
    function notificationRules(raw) {
        return ("" + (raw || "")).split(/[\n,;]+/).map(function (v) { return v.trim(); })
            .filter(function (v) { return v !== ""; });
    }
    // Case-insensitive exact match with shell-style * and ? wildcards.
    function globMatches(value, pattern) {
        var text = ("" + (value || "")).toLowerCase();
        var rule = ("" + (pattern || "")).toLowerCase();
        var ti = 0, ri = 0, star = -1, retry = 0;
        while (ti < text.length) {
            if (ri < rule.length && (rule.charAt(ri) === "?" || rule.charAt(ri) === text.charAt(ti))) {
                ti++; ri++; continue;
            }
            if (ri < rule.length && rule.charAt(ri) === "*") {
                star = ri++; retry = ti; continue;
            }
            if (star >= 0) { ri = star + 1; ti = ++retry; continue; }
            return false;
        }
        while (ri < rule.length && rule.charAt(ri) === "*") ri++;
        return ri === rule.length;
    }
    function matchesNotificationRule(container, rule) {
        if (!container) return false;
        var name = containerName(container);
        var project = container.project || "";
        var service = container.service || "";
        var targets = [name, project, service];
        if (project && service) targets.push(project + "/" + service);
        for (var i = 0; i < targets.length; i++)
            if (targets[i] && globMatches(targets[i], rule)) return true;
        return false;
    }
    function notificationAllowed(server, kind, container) {
        if (!notificationCategoryEnabled(server, kind)) return false;
        // Project/container filters never suppress host, disk, or certificate alerts.
        if (!container) return true;
        var only = notificationRules(server.notifyOnly);
        var muted = notificationRules(server.notifyMute);
        var allowed = only.length === 0;
        for (var i = 0; i < only.length && !allowed; i++)
            allowed = matchesNotificationRule(container, only[i]);
        if (!allowed) return false;
        for (var j = 0; j < muted.length; j++)
            if (matchesNotificationRule(container, muted[j])) return false;
        return true;
    }
    function isLiveContainer(c) {
        var state = (c && c.state ? c.state : "").toLowerCase();
        return state === "running" || state === "restarting" || state === "paused";
    }
    function isRecentCrash(c, nowMs) {
        if (!c || (c.state || "").toLowerCase() !== "exited" || Number(c.exitCode || 0) === 0) return false;
        var finished = Date.parse(c.finishedAt || "");
        if (isNaN(finished)) return false;
        var windowMs = Math.max(5, Plasmoid.configuration.fleetRestartWindowMinutes || 60) * 60000;
        return finished <= (nowMs || Date.now()) && (nowMs || Date.now()) - finished <= windowMs;
    }

    readonly property string scriptPath:
        Qt.resolvedUrl("../code/dockswain.sh").toString().replace(/^file:\/\//, "")
    function shq(s) { return "'" + ("" + s).replace(/'/g, "'\\''") + "'"; }

    // A single executable engine owned by the controller. Plasma does not
    // reliably deliver executable-engine results from DataSources nested inside
    // Instantiator delegates, so per-host sessions dispatch through this bridge.
    Plasma5Support.DataSource {
        id: commandEngine
        engine: "executable"
        connectedSources: []
        property var callbacks: ({})
        onNewData: (source, data) => {
            var callback = callbacks[source];
            if (callback) {
                delete callbacks[source];
                callback("" + (data["stdout"] || ""), Number(data["exit code"]));
            }
            disconnectSource(source);
        }
        function run(command, callback) {
            if (!command || callbacks[command]) return false;
            callbacks[command] = callback || function () {};
            connectSource(command);
            return true;
        }
        function post(title, detail, critical) {
            var cmd = "bash " + fleet.shq(fleet.scriptPath) + " notify "
                    + fleet.shq(title) + " " + fleet.shq(detail) + " "
                    + (critical ? "critical" : "normal");
            run(cmd, null);
        }
    }
    function runCommand(command, callback) { return commandEngine.run(command, callback); }

    Instantiator {
        id: monitorPool
        model: fleet.servers.length
        delegate: FleetHostSession {
            mainRoot: fleet
            serverIndex: index
            server: fleet.servers[index] || null
        }
    }

    function persistSnapshots() {
        // Samples for several hosts normally arrive together. Coalesce them into
        // one KConfig update instead of rewriting the full snapshot per host.
        snapshotPersistTimer.restart();
    }
    function persistEvents() {
        var limit = Math.max(50, Plasmoid.configuration.fleetEventHistoryLimit || 250);
        if (events.length > limit) events = events.slice(0, limit);
        Plasmoid.configuration.fleetEventsJson = JSON.stringify(events);
    }
    function appendEvent(kind, severity, serverIndex, container, title, detail, count) {
        var s = servers[serverIndex] || null;
        var event = {
            id: Date.now() + "-" + Math.floor(Math.random() * 1000000),
            timestamp: Date.now(), kind: kind, severity: severity,
            serverKey: serverKey(s), serverLabel: serverLabel(s),
            containerId: container ? (container.id || "") : "",
            containerName: container ? containerName(container) : "",
            project: container ? (container.project || "") : "",
            service: container ? (container.service || "") : "",
            title: title || kind, detail: detail || "", count: Number(count || 1)
        };
        var next = [event].concat(events || []);
        var limit = Math.max(50, Plasmoid.configuration.fleetEventHistoryLimit || 250);
        events = next.slice(0, limit);
        persistEvents();
        var restartAlert = true;
        if (kind === "container_restart") {
            var recent = restartCount(event.serverKey, event.containerId);
            var threshold = Plasmoid.configuration.fleetRestartThreshold;
            restartAlert = recent >= threshold && recent - event.count < threshold;
        }
        if (restartAlert && Plasmoid.configuration.fleetNotifications
                && notificationAllowed(s, kind, container)
                && (!isRecoveryKind(kind) || Plasmoid.configuration.fleetNotifyRecovery)
                && severity !== "info") {
            commandEngine.post(event.title,
                event.serverLabel + (event.detail ? " · " + event.detail : ""), severity === "critical");
        }
    }

    function receiveHealth(serverIndex, data) {
        var s = servers[serverIndex];
        if (!s) return;
        var key = serverKey(s), old = snapshots[key] || null;
        var next = clone(old || {}), now = Date.now();
        next.serverKey = key; next.serverLabel = serverLabel(s); next.serverIndex = serverIndex;
        next.sampledAt = now; next.healthObserved = true;
        next.ok = !!(data && data.ok); next.reachable = !!(data && data.reachable);
        next.dockerOk = !!(data && data.dockerOk);
        next.reason = data ? (data.reason || "") : "parse_error";
        next.version = data ? (data.version || "") : "";
        next.imageAwarenessVersion = data ? Number(data.imageAwarenessVersion || 0) : 0;
        next.containers = data && data.ok ? (data.containers || []) : [];
        next.host = data && data.ok ? (data.host || {}) : {};
        next.images = data && data.ok ? (data.images || []) : [];

        if (old && old.healthObserved) detectHealthTransitions(serverIndex, old, next);
        var all = clone(snapshots); all[key] = next; snapshots = all;
        persistSnapshots(); rebuildModels();
    }

    function detectHealthTransitions(serverIndex, old, next) {
        var label = next.serverLabel;
        if (old.reachable && !next.reachable) {
            appendEvent("host_offline", "critical", serverIndex, null,
                i18n("%1 is offline", label), next.reason || i18n("SSH is unreachable"));
        } else if (!old.reachable && next.reachable) {
            appendEvent("host_online", "ok", serverIndex, null,
                i18n("%1 is back online", label), i18n("SSH connection recovered"));
        }
        if (old.reachable && old.dockerOk && next.reachable && !next.dockerOk) {
            appendEvent("docker_unavailable", "critical", serverIndex, null,
                i18n("Docker unavailable on %1", label), next.reason || i18n("Docker check failed"));
        } else if (old.reachable && !old.dockerOk && next.dockerOk) {
            appendEvent("docker_recovered", "ok", serverIndex, null,
                i18n("Docker recovered on %1", label), i18n("Docker is responding again"));
        }
        if (!old.dockerOk || !next.dockerOk) return;

        var before = byId(old.containers), current = byId(next.containers);
        Object.keys(current).forEach(function (id) {
            var c = current[id], b = before[id];
            if (!b) {
                appendEvent("container_created", "info", serverIndex, c,
                    i18n("%1 was created", containerName(c)), c.imageRef || c.image || "");
                return;
            }
            var delta = Number(c.restartCount || 0) - Number(b.restartCount || 0);
            if (delta > 0) appendEvent("container_restart", "warning", serverIndex, c,
                i18n("%1 restarted", containerName(c)),
                i18np("%1 new restart", "%1 new restarts", delta), delta);

            var bh = healthOf(b), ch = healthOf(c);
            if (bh !== "unhealthy" && ch === "unhealthy")
                appendEvent("container_unhealthy", "critical", serverIndex, c,
                    i18n("%1 became unhealthy", containerName(c)), c.status || "");
            else if (bh === "unhealthy" && ch !== "unhealthy"
                     && (c.state || "").toLowerCase() === "running")
                appendEvent("container_recovered", "ok", serverIndex, c,
                    i18n("%1 recovered", containerName(c)), c.status || "");

            var bs = (b.state || "").toLowerCase(), cs = (c.state || "").toLowerCase();
            var wasLive = bs === "running" || bs === "restarting" || bs === "paused";
            var isLive = cs === "running" || cs === "restarting" || cs === "paused";
            if (bs !== "restarting" && cs === "restarting") {
                appendEvent("container_restarting", "warning", serverIndex, c,
                    i18n("%1 is restarting", containerName(c)), c.status || cs);
            }
            if (wasLive && !isLive) {
                var crashed = Number(c.exitCode || 0) !== 0;
                appendEvent(crashed ? "container_crashed" : "container_stopped",
                    crashed ? "critical" : "warning", serverIndex, c,
                    crashed ? i18n("%1 crashed", containerName(c)) : i18n("%1 stopped", containerName(c)),
                    c.status || cs);
            } else if (!wasLive && isLive) {
                appendEvent("container_started", "info", serverIndex, c,
                    i18n("%1 started", containerName(c)), c.status || cs);
            }

            if (Plasmoid.configuration.fleetResourceMonitoring) {
                detectThreshold(serverIndex, b, c, "cpu", Plasmoid.configuration.fleetCpuThreshold,
                    "container_cpu_high", "container_cpu_recovered", i18n("CPU"));
                detectThreshold(serverIndex, b, c, "mem", Plasmoid.configuration.fleetMemoryThreshold,
                    "container_memory_high", "container_memory_recovered", i18n("memory"));
            }
            if (Plasmoid.configuration.fleetImageMonitoring
                    && Number(old.imageAwarenessVersion || 0) >= 2
                    && Number(next.imageAwarenessVersion || 0) >= 2
                    && isLiveContainer(c) && !c.imagePinned) {
                if (!b.imageUpdate && c.imageUpdate)
                    appendEvent("image_update", "warning", serverIndex, c,
                        i18n("Newer local image for %1", containerName(c)), c.imageRef || c.image || "");
                else if (b.imageUpdate && !c.imageUpdate)
                    appendEvent("image_updated", "ok", serverIndex, c,
                        i18n("%1 now uses the current image", containerName(c)), c.imageRef || c.image || "");
            }
        });
        Object.keys(before).forEach(function (id) {
            if (!current[id]) appendEvent("container_removed", "info", serverIndex, before[id],
                i18n("%1 was removed", containerName(before[id])), before[id].imageRef || before[id].image || "");
        });
        if (Plasmoid.configuration.fleetResourceMonitoring) {
            var threshold = Plasmoid.configuration.fleetMemoryThreshold;
            var wasHigh = Number((old.host || {}).memoryPct || 0) >= threshold;
            var isHigh = Number((next.host || {}).memoryPct || 0) >= threshold;
            if (!wasHigh && isHigh) appendEvent("host_memory_high", "warning", serverIndex, null,
                i18n("High memory use on %1", label), Math.round(next.host.memoryPct) + "%");
            else if (wasHigh && !isHigh) appendEvent("host_memory_recovered", "ok", serverIndex, null,
                i18n("Memory recovered on %1", label), Math.round(next.host.memoryPct) + "%");
        }
    }

    function detectThreshold(serverIndex, before, current, field, threshold, highKind, recoveryKind, label) {
        if (before[field] === undefined || before[field] === null || before[field] === ""
                || current[field] === undefined || current[field] === null || current[field] === "") return;
        var wasHigh = crosses(before[field], threshold), isHigh = crosses(current[field], threshold);
        if (!wasHigh && isHigh) appendEvent(highKind, "warning", serverIndex, current,
            i18n("High %1: %2", label, containerName(current)),
            field === "cpu" ? i18n("%1 · 100% equals one CPU core", current[field] || "") : (current[field] || ""));
        else if (wasHigh && !isHigh) appendEvent(recoveryKind, "ok", serverIndex, current,
            i18n("%1 recovered: %2", label, containerName(current)), current[field] || "");
    }

    function receiveMeta(serverIndex, data) {
        var s = servers[serverIndex];
        if (!s) return;
        var key = serverKey(s), old = snapshots[key] || null, next = clone(old || {});
        next.serverKey = key; next.serverLabel = serverLabel(s); next.serverIndex = serverIndex;
        next.metaSampledAt = Date.now(); next.metaObserved = !!(data && data.ok);
        if (data && data.ok) {
            next.disk = data.disk || null; next.df = data.df || [];
            next.certbot = data.certbot !== false; next.certs = data.certs || [];
        }
        if (old && old.metaObserved && data && data.ok) detectMetaTransitions(serverIndex, old, next);
        var all = clone(snapshots); all[key] = next; snapshots = all;
        persistSnapshots(); rebuildModels();
    }

    function receiveFleetHealth(serverIndex, data) { receiveHealth(serverIndex, data); }
    function receiveFleetMeta(serverIndex, data) { receiveMeta(serverIndex, data); }

    function detectMetaTransitions(serverIndex, old, next) {
        if (Plasmoid.configuration.fleetDiskMonitoring && old.disk && next.disk) {
            var threshold = Plasmoid.configuration.fleetDiskThreshold;
            var wasHigh = crosses(old.disk.usePct, threshold), isHigh = crosses(next.disk.usePct, threshold);
            if (!wasHigh && isHigh) appendEvent("disk_pressure", "warning", serverIndex, null,
                i18n("Disk pressure on %1", next.serverLabel), next.disk.usePct || "");
            else if (wasHigh && !isHigh) appendEvent("disk_recovered", "ok", serverIndex, null,
                i18n("Disk recovered on %1", next.serverLabel), next.disk.usePct || "");
        }
        if (!Plasmoid.configuration.fleetSslMonitoring) return;
        var before = {};
        (old.certs || []).forEach(function (c) { before[c.name || c.domains] = c; });
        (next.certs || []).forEach(function (c) {
            var id = c.name || c.domains, b = before[id];
            if (!b) return;
            var bd = Fmt.certificateDays(b.expiry, Number(old.metaSampledAt || Date.now()));
            var nd = Fmt.certificateDays(c.expiry);
            var limit = Plasmoid.configuration.fleetSslWarningDays;
            if (bd > limit && nd <= limit) appendEvent("ssl_expiring", nd < 0 ? "critical" : "warning", serverIndex, null,
                i18n("Certificate expiring: %1", c.domains || c.name),
                nd < 0 ? i18np("Expired %1 day ago", "Expired %1 days ago", Math.abs(nd))
                       : i18np("%1 day remaining", "%1 days remaining", nd));
            else if (bd <= limit && nd > limit) appendEvent("ssl_recovered", "ok", serverIndex, null,
                i18n("Certificate renewed: %1", c.domains || c.name), i18np("%1 day remaining", "%1 days remaining", nd));
        });
    }

    function restartCount(serverKeyValue, containerId) {
        var cutoff = Date.now() - Math.max(5, Plasmoid.configuration.fleetRestartWindowMinutes || 60) * 60000;
        var count = 0;
        (events || []).forEach(function (e) {
            if (e.timestamp >= cutoff && e.kind === "container_restart"
                    && e.serverKey === serverKeyValue && e.containerId === containerId)
                count += Number(e.count || 1);
        });
        return count;
    }
    function reclaimable(df) {
        var total = 0;
        (df || []).forEach(function (r) { total += Fmt.dockerBytes(r.reclaimable); });
        return total;
    }
    function addProblem(severity, kind, serverIndex, serverLabelValue, container, title, detail, feature) {
        problemsModel.append({
            severity:severity, kind:kind, serverIndex:serverIndex,
            serverLabel:serverLabelValue, containerId:container ? (container.id || "") : "",
            containerName:container ? containerName(container) : "", title:title, detail:detail || "",
            feature:feature || "", restartable:!!container
        });
    }

    function rebuildModels() {
        hostsModel.clear(); problemsModel.clear(); certificatesModel.clear(); imagesModel.clear(); eventsModel.clear();
        observedCount = 0; onlineCount = 0; healthyCount = 0; warningCount = 0; unhealthyCount = 0;
        imageUpdateCount = 0; unpinnedImageCount = 0; expiringCertCount = 0;
        var now = Date.now(), seenImages = {};
        for (var i = 0; i < servers.length; i++) {
            var s = servers[i], key = serverKey(s), snap = snapshots[key] || null;
            var observed = !!(snap && snap.healthObserved);
            var online = !!(observed && snap.reachable), docker = !!(observed && snap.dockerOk);
            if (observed) observedCount++;
            if (online) onlineCount++;
            var hr = 0, hw = 0, hb = 0, total = 0, running = 0;
            if (!observed) {
                // Initial collection is progress, not an operational warning.
            } else if (!online) {
                var connectionKind = snap.reason === "no_password" ? "credentials"
                                   : snap.reason === "parse_error" ? "monitor_error" : "host_offline";
                var connectionSeverity = connectionKind === "host_offline" ? "critical" : "warning";
                var connectionTitle = connectionKind === "credentials" ? i18n("Credentials required")
                                    : connectionKind === "monitor_error" ? i18n("Monitoring failed")
                                    : i18n("Host offline");
                addProblem(connectionSeverity, connectionKind, i, serverLabel(s), null,
                    connectionTitle, reasonText(snap.reason) || i18n("SSH is unreachable"), "");
            } else if (!docker) {
                addProblem("critical", "docker_unavailable", i, serverLabel(s), null,
                    i18n("Docker unavailable"), reasonText(snap.reason) || i18n("Docker is not responding"), "");
            } else {
                var hostMem = Number((snap.host || {}).memoryPct || 0);
                if (Plasmoid.configuration.fleetResourceMonitoring
                        && hostMem >= Plasmoid.configuration.fleetMemoryThreshold)
                    addProblem("warning", "host_memory_high", i, serverLabel(s), null,
                        i18n("Host memory %1%", Math.round(hostMem)), i18n("Memory threshold exceeded"), "");

                (snap.containers || []).forEach(function (c) {
                    total++;
                    var state = (c.state || "").toLowerCase(), health = healthOf(c);
                    var live = isLiveContainer(c);
                    var cpuHigh = Plasmoid.configuration.fleetResourceMonitoring
                               && live
                               && crosses(c.cpu, Plasmoid.configuration.fleetCpuThreshold);
                    var memHigh = Plasmoid.configuration.fleetResourceMonitoring
                               && live
                               && crosses(c.mem, Plasmoid.configuration.fleetMemoryThreshold);
                    var recent = restartCount(key, c.id || "");
                    var restartHigh = recent >= Plasmoid.configuration.fleetRestartThreshold;
                    var imageHigh = Plasmoid.configuration.fleetImageMonitoring
                                 && live && !c.imagePinned && !!c.imageUpdate;
                    var recentCrash = isRecentCrash(c, now);
                    if (state === "running") running++;
                    if ((live && health === "unhealthy") || state === "dead") {
                        hb++;
                        addProblem("critical", "container_unhealthy", i, serverLabel(s), c,
                            i18n("%1 is unhealthy", containerName(c)), c.status || "", "containers");
                    } else if (state === "restarting" || cpuHigh || memHigh || restartHigh || imageHigh
                               || recentCrash) {
                        hw++;
                        if (state === "restarting") addProblem("warning", "container_restarting", i, serverLabel(s), c,
                            i18n("%1 is restarting", containerName(c)), c.status || "", "containers");
                        if (restartHigh) addProblem("warning", "restart_burst", i, serverLabel(s), c,
                            i18n("%1 restarted %2 times", containerName(c), recent),
                            i18np("within %1 minute", "within %1 minutes", Plasmoid.configuration.fleetRestartWindowMinutes), "containers");
                        if (cpuHigh) addProblem("warning", "container_cpu_high", i, serverLabel(s), c,
                            i18n("%1 CPU %2", containerName(c), c.cpu),
                            i18n("100% equals one fully used CPU core"), "containers");
                        if (memHigh) addProblem("warning", "container_memory_high", i, serverLabel(s), c,
                            i18n("%1 memory %2", containerName(c), c.mem), c.memUsage || i18n("Memory threshold exceeded"), "containers");
                        if (imageHigh) addProblem("warning", "image_update", i, serverLabel(s), c,
                            i18n("%1 uses an older image", containerName(c)), c.imageRef || c.image || "", "containers");
                        if (recentCrash)
                            addProblem("warning", "container_crashed", i, serverLabel(s), c,
                                i18n("%1 exited with code %2", containerName(c), c.exitCode), c.status || "", "containers");
                    } else if (state === "running" || state === "paused") hr++;

                    var ref = c.imageRef || c.image || "";
                    if (live && ref && !seenImages[key + "|" + ref]) {
                        seenImages[key + "|" + ref] = true;
                        var pinned = !!c.imagePinned, updated = !pinned && !!c.imageUpdate;
                        if (!pinned) unpinnedImageCount++;
                        if (updated) imageUpdateCount++;
                        imagesModel.append({serverIndex:i,serverLabel:serverLabel(s),ref:ref,
                            pinned:pinned,updateAvailable:updated,created:c.imageCreated || ""});
                    }
                });
            }

            if (snap && snap.disk && Plasmoid.configuration.fleetDiskMonitoring
                    && crosses(snap.disk.usePct, Plasmoid.configuration.fleetDiskThreshold)) {
                var rec = reclaimable(snap.df);
                addProblem(Fmt.pctNumber(snap.disk.usePct) >= 95 ? "critical" : "warning",
                    "disk_pressure", i, serverLabel(s), null,
                    i18n("Disk %1", snap.disk.usePct || ""),
                    rec > 0 ? i18n("%1 reclaimable", Fmt.fmtBytes(rec)) : i18n("Disk threshold exceeded"), "disk");
            }
            (snap && snap.certs ? snap.certs : []).forEach(function (c) {
                var days = Fmt.certificateDays(c.expiry, now);
                if (!(c.domains || c.name) || days === 999999) return;
                certificatesModel.append({serverIndex:i,serverLabel:serverLabel(s),name:c.name || "",
                    domains:c.domains || "",expiry:c.expiry || "",days:days});
                if (Plasmoid.configuration.fleetSslMonitoring
                        && days <= Plasmoid.configuration.fleetSslWarningDays) {
                    expiringCertCount++;
                    addProblem(days < 0 ? "critical" : "warning", "ssl_expiring", i, serverLabel(s), null,
                        days < 0 ? i18n("SSL expired: %1", c.domains || c.name)
                                 : i18n("SSL expires in %1d: %2", days, c.domains || c.name),
                        c.expiry || "", "nginx");
                }
            });
            hostsModel.append({serverIndex:i,label:serverLabel(s),observed:observed,
                reachable:online,dockerOk:docker,
                reasonCode:snap ? (snap.reason || "") : "",
                reason:snap ? reasonText(snap.reason) : "",running:running,total:total,healthy:hr,
                warnings:hw,unhealthy:hb,diskPct:snap && snap.disk ? (snap.disk.usePct || "") : "",
                memoryPct:snap && snap.host ? Number(snap.host.memoryPct || 0) : 0,
                sampledAt:snap ? Number(snap.sampledAt || 0) : 0});
            healthyCount += hr; warningCount += hw; unhealthyCount += hb;
        }
        var orderedCerts = [];
        // ListModel.get() returns a live proxy. Clone before clear(), otherwise
        // those proxies turn into blank strings/zeroes (the visible ". / 0 days" bug).
        for (var ci = 0; ci < certificatesModel.count; ci++) orderedCerts.push(clone(certificatesModel.get(ci)));
        orderedCerts.sort(function(a,b){ return a.days-b.days; });
        certificatesModel.clear(); orderedCerts.forEach(function(c){ certificatesModel.append(c); });
        (events || []).forEach(function (e) {
            var si = -1;
            for (var j = 0; j < servers.length; j++) if (serverKey(servers[j]) === e.serverKey) { si = j; break; }
            eventsModel.append({timestamp:Number(e.timestamp || 0),kind:e.kind || "",severity:e.severity || "info",
                serverIndex:si,serverLabel:e.serverLabel || "",containerName:e.containerName || "",
                title:e.title || e.kind || "",detail:e.detail || ""});
        });
        problemCount = problemsModel.count;
        criticalProblemCount = 0; warningProblemCount = 0;
        for (var pi = 0; pi < problemsModel.count; pi++) {
            if (problemsModel.get(pi).severity === "critical") criticalProblemCount++;
            else warningProblemCount++;
        }
        loading = observedCount < servers.length;
    }

    function refreshAll() {
        for (var i = 0; i < monitorPool.count; i++) {
            var m = monitorPool.objectAt(i); if (m) m.refreshAll();
        }
    }
    function refreshHost(serverIndex) {
        var m = monitorPool.objectAt(serverIndex); if (m) m.refreshAll();
    }
    function restartContainer(serverIndex, id) {
        var m = monitorPool.objectAt(serverIndex); if (m) m.containerAction("restart", id);
    }
    function clearEvents() {
        events = []; persistEvents(); rebuildModels();
    }
    function pruneSnapshots() {
        var valid = {}, cleaned = {}, changed = false;
        (servers || []).forEach(function (s) { valid[serverKey(s)] = true; });
        Object.keys(snapshots || {}).forEach(function (key) {
            if (valid[key]) cleaned[key] = snapshots[key]; else changed = true;
        });
        if (changed) { snapshots = cleaned; persistSnapshots(); }
    }

    onServersChanged: { pruneSnapshots(); rebuildModels(); }
    Component.onCompleted: rebuildModels()
}
