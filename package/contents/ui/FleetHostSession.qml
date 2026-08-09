import QtQuick
import org.kde.plasma.plasmoid

// Background monitor for one configured host. It is deliberately independent of
// the interactive ServerSession/open-tab pool: every configured server remains
// observed even when its management tab is closed.
Item {
    id: monitor

    property var mainRoot
    property var server: null
    property int serverIndex: -1
    property bool healthBusy: false
    property bool metaBusy: false

    function shq(s) { return "'" + ("" + s).replace(/'/g, "'\\''") + "'"; }
    readonly property string scriptPath:
        Qt.resolvedUrl("../code/dockswain.sh").toString().replace(/^file:\/\//, "")

    function envPrefix() {
        var p = "";
        var dc = Plasmoid.configuration.dockerCmd;
        if (dc && dc !== "docker") p += "CNQ_DOCKER_CMD=" + shq(dc) + " ";
        p += "CNQ_SSH_TIMEOUT=" + (Plasmoid.configuration.sshConnectTimeout || 5) + " ";
        p += "CNQ_AUTH=" + ((server && server.auth) ? server.auth : "key") + " ";
        p += "CNQ_NGINX_DIR=" + shq(Plasmoid.configuration.nginxDir || "/etc/nginx") + " ";
        if (server && server.useSudo) p += "CNQ_SUDO=1 ";
        return p;
    }
    function helperCmd(sub, extra) {
        if (!server) return "";
        var target = server.user ? (server.user + "@" + server.host) : server.host;
        var c = envPrefix() + "bash " + shq(scriptPath) + " " + sub + " "
              + shq(target) + " " + (server.port || 22) + " " + shq(server.key || "");
        return extra ? c + " " + extra : c;
    }

    function parse(out, fallback) {
        try { return JSON.parse((out || "").trim()); }
        catch (e) { return fallback; }
    }
    function refreshHealth() {
        if (!server || healthBusy) return;
        healthBusy = true;
        var stats = Plasmoid.configuration.fleetResourceMonitoring ? "1" : "0";
        if (!mainRoot.runCommand(helperCmd("fleet-health", stats), function (out) {
            healthBusy = false;
            mainRoot.receiveFleetHealth(serverIndex, parse(out, {
                ok:false, reachable:false, dockerOk:false, reason:"parse_error"
            }));
        })) healthBusy = false;
    }
    function refreshMeta() {
        if (!server || metaBusy) return;
        metaBusy = true;
        var disk = Plasmoid.configuration.fleetDiskMonitoring ? "1" : "0";
        var ssl = Plasmoid.configuration.fleetSslMonitoring ? "1" : "0";
        if (!mainRoot.runCommand(helperCmd("fleet-meta", disk + " " + ssl), function (out) {
            metaBusy = false;
            mainRoot.receiveFleetMeta(serverIndex, parse(out, {ok:false, reason:"parse_error"}));
        })) metaBusy = false;
    }
    function refreshAll() { refreshHealth(); refreshMeta(); }
    function containerAction(action, id) {
        if (!server || !/^[0-9a-fA-F]{12,64}$/.test(id || "")) return;
        mainRoot.runCommand(helperCmd("action", action + " " + shq(id)), function () { refreshHealth(); });
    }

    onServerChanged: if (server) refreshAll()
    Component.onCompleted: if (server) refreshAll()

    Timer {
        interval: Math.max(10, Plasmoid.configuration.fleetPollInterval) * 1000
        running: monitor.server !== null
        repeat: true
        onTriggered: monitor.refreshHealth()
    }
    Timer {
        interval: Math.max(300, Plasmoid.configuration.fleetDeepInterval) * 1000
        running: monitor.server !== null
        repeat: true
        onTriggered: monitor.refreshMeta()
    }
}
