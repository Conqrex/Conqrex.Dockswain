import QtQuick
import QtQuick.Layouts
import QtQuick.Controls as QQC2
import org.kde.kcmutils as KCM
import org.kde.kirigami as Kirigami
import org.kde.plasma.plasma5support as Plasma5Support
import org.kde.prison as Prison

KCM.SimpleKCM {
    id: page

    property string cfg_serversJson: "[]"
    property string cfg_dockerCmd: "docker"
    property string cfg_nginxDir: "/etc/nginx"
    property int cfg_sshConnectTimeout: 5
    property bool exportSecrets: false
    property string qrPayload: ""
    property string qrSummary: ""
    property string qrWarnings: ""

    function shq(s) { return "'" + ("" + s).replace(/'/g, "'\\''") + "'"; }
    function secretKey(user, host, port) {
        return (user ? user + "@" : "") + host + ":" + port;
    }

    property string importNote: ""
    function scriptPath() { return Qt.resolvedUrl("../code/dockswain.sh").toString().replace(/^file:\/\//, ""); }

    ListModel { id: serversModel }

    function load() {
        serversModel.clear();
        try {
            JSON.parse(page.cfg_serversJson || "[]").forEach(function (s) {
                serversModel.append({
                    label: s.label || "", user: s.user || "", host: s.host || "",
                    port: (s.port || 22), key: s.key || "", auth: s.auth || "key",
                    remmina: s.remmina || "", hasSecret: s.hasSecret || false,
                    useSudo: s.useSudo || false
                });
            });
        } catch (e) {}
    }
    function save() {
        var arr = [];
        for (var i = 0; i < serversModel.count; i++) {
            var r = serversModel.get(i);
            arr.push({ label: r.label, user: r.user, host: r.host, port: r.port, key: r.key, auth: r.auth,
                       remmina: r.remmina || "", hasSecret: r.hasSecret || false, useSudo: r.useSudo || false });
        }
        page.cfg_serversJson = JSON.stringify(arr);
    }
    Component.onCompleted: load()

    Plasma5Support.DataSource {
        id: importer
        engine: "executable"
        connectedSources: []
        onNewData: (source, data) => {
            disconnectSource(source);
            try {
                var r = JSON.parse(("" + (data["stdout"] || "")).trim());
                if (r && r.ok && r.hosts) {
                    r.hosts.forEach(function (h) {
                        var dupIdx = -1;
                        for (var i = 0; i < serversModel.count; i++) {
                            var e = serversModel.get(i);
                            if (e.host === h.host && e.port === (h.port || 22)) dupIdx = i;
                        }
                        if (dupIdx >= 0) {
                            // refresh the Remmina link/secret state on re-import
                            serversModel.setProperty(dupIdx, "remmina", h.remmina || "");
                            serversModel.setProperty(dupIdx, "hasSecret", h.hasSecret || false);
                        } else {
                            serversModel.append({
                                label: h.label || "", user: h.user || "", host: h.host || "",
                                port: (h.port || 22), key: h.key || "", auth: h.auth || "key",
                                remmina: h.remmina || "", hasSecret: h.hasSecret || false,
                                useSudo: false
                            });
                        }
                        if (h.filezilla) page.storeFzPass(h);    // copy FileZilla pass into the keyring
                    });
                    page.save();
                    if (r.ftp !== undefined)                     // FileZilla import: report skipped FTP
                        page.importNote = i18n("Imported %1 SFTP site(s). Skipped %2 FTP site(s) — this widget connects over SSH only.", r.sftp || 0, r.ftp || 0);
                }
            } catch (e) {}
        }
        function go(sub) {
            page.importNote = "";
            connectSource("bash '" + page.scriptPath() + "' " + (sub || "hosts"));
        }
    }

    Plasma5Support.DataSource {
        id: qrExporter
        engine: "executable"
        connectedSources: []
        onNewData: (source, data) => {
            disconnectSource(source);
            try {
                var r = JSON.parse(("" + (data["stdout"] || "")).trim());
                if (r && r.ok) {
                    page.qrPayload = r.payload || "";
                    page.qrSummary = i18n("QR contains %1 server(s). Included %2 secret(s).", r.count || 0, r.secrets || 0);
                    page.qrWarnings = (r.warnings || []).join("\n");
                    qrDialog.open();
                } else {
                    page.importNote = i18n("Could not create mobile QR: %1", r ? (r.reason || "error") : "error");
                }
            } catch (e) {
                page.importNote = i18n("Could not create mobile QR.");
            }
        }
        function show(index) {
            page.save();
            var which = (index === undefined || index < 0) ? "all" : ("" + index);
            var cmd = "bash " + page.shq(page.scriptPath())
                    + " mobile-export " + (page.exportSecrets ? "1" : "0")
                    + " " + page.shq(which)
                    + " " + page.shq(page.cfg_serversJson || "[]")
                    + " " + page.shq(page.cfg_dockerCmd || "docker")
                    + " " + page.shq(page.cfg_nginxDir || "/etc/nginx")
                    + " " + (page.cfg_sshConnectTimeout || 5);
            connectSource(cmd);
        }
    }

    // fire-and-forget runner for secret-tool store/clear
    Plasma5Support.DataSource {
        id: runner
        engine: "executable"
        connectedSources: []
        onNewData: (source, data) => disconnectSource(source)
        function exec(c) { if (c) connectSource(c); }
    }

    // Detects the current username for a freshly-added local server row, so the
    // user@host field is filled with <you>@localhost instead of being hardcoded.
    Plasma5Support.DataSource {
        id: localUser
        engine: "executable"
        connectedSources: []
        property int targetIndex: -1
        onNewData: (source, data) => {
            disconnectSource(source);
            var u = ("" + (data["stdout"] || "")).trim();
            if (u && targetIndex >= 0 && targetIndex < serversModel.count) {
                serversModel.setProperty(targetIndex, "user", u);
                page.save();
            }
            targetIndex = -1;
        }
        function detect(idx) { targetIndex = idx; connectSource("id -un"); }
    }

    // Appends a ready-to-use local Docker server (SSH to localhost, key/agent auth)
    // and fills in the current username. Connecting still needs sshd running and the
    // key usable non-interactively — the inline note below spells that out.
    function addLocal() {
        // reuse an existing localhost row rather than piling up duplicates
        for (var i = 0; i < serversModel.count; i++) {
            var e = serversModel.get(i);
            if (e.host === "localhost" && e.port === 22) { localUser.detect(i); return; }
        }
        serversModel.append({ label: i18n("Local Docker"), user: "", host: "localhost",
            port: 22, key: "", auth: "key", remmina: "", hasSecret: false, useSudo: false });
        page.save();
        localUser.detect(serversModel.count - 1);
    }

    function setPassword(label, user, host, port) {
        var key = page.secretKey(user, host, port);
        var script = "secret-tool store --label=" + page.shq("Dockswain — " + (label || key))
                   + " service cnq-dockswain host " + page.shq(key)
                   + " && echo && echo 'Password saved. You can close this window.'"
                   + " || echo 'Cancelled / failed.'";
        runner.exec("konsole --hold -e bash -c " + page.shq(script));
    }
    function clearPassword(user, host, port) {
        var key = page.secretKey(user, host, port);
        runner.exec("secret-tool clear service cnq-dockswain host " + page.shq(key));
    }
    // pipe a FileZilla site's decoded password into the keyring under our schema.
    // imported SFTP password sites then work with no copy-paste (like the Remmina flow).
    function storeFzPass(h) {
        if (!h || h.auth !== "password" || !h.hasSecret) return;
        var key = page.secretKey(h.user, h.host, h.port || 22);
        // capture the decoded password; only store when non-empty, otherwise a failed/garbled
        // decode would leave a blank credential in the keyring.
        var cmd = "pw=$(bash " + page.shq(page.scriptPath()) + " filezilla-pass "
                + page.shq(h.host) + " " + (h.port || 22) + " " + page.shq(h.user || "") + "); "
                + "[ -n \"$pw\" ] && printf %s \"$pw\" | secret-tool store --label="
                + page.shq("Dockswain — " + (h.label || key))
                + " service cnq-dockswain host " + page.shq(key);
        runner.exec("bash -c " + page.shq(cmd));
    }

    ColumnLayout {
        width: parent.width
        spacing: Kirigami.Units.smallSpacing

        RowLayout {
            Layout.fillWidth: true
            QQC2.Button {
                text: i18n("Add server")
                icon.name: "list-add"
                onClicked: { serversModel.append({ label: "", user: "root", host: "", port: 22, key: "", auth: "password", remmina: "", hasSecret: false, useSudo: false }); page.save(); }
            }
            QQC2.Button {
                text: i18n("Add local")
                icon.name: "computer"
                QQC2.ToolTip.visible: hovered
                QQC2.ToolTip.text: i18n("Add this machine — SSH to localhost with key/agent auth. Needs sshd running and passwordless SSH to yourself.")
                onClicked: page.addLocal()
            }
            QQC2.Button {
                text: i18n("Import from Remmina")
                icon.name: "document-import"
                onClicked: importer.go("hosts")
            }
            QQC2.Button {
                text: i18n("Import from FileZilla")
                icon.name: "document-import"
                onClicked: importer.go("filezilla-hosts")
            }
            QQC2.Button {
                text: i18n("Mobile QR (all)")
                icon.name: "view-barcode-qr"
                enabled: serversModel.count > 0
                onClicked: qrExporter.show(-1)
            }
            Item { Layout.fillWidth: true }
        }

        QQC2.CheckBox {
            id: secretsBox
            Layout.fillWidth: true
            text: i18n("Include SSH passwords / private keys in mobile QR codes")
            checked: page.exportSecrets
            onToggled: page.exportSecrets = checked
        }

        Kirigami.InlineMessage {
            Layout.fillWidth: true
            visible: page.exportSecrets
            type: Kirigami.MessageType.Warning
            text: i18n("Secrets in a QR code are visible plaintext. Only show this code to your own phone and close it after importing.")
        }

        Kirigami.InlineMessage {
            Layout.fillWidth: true
            visible: page.importNote !== ""
            type: Kirigami.MessageType.Information
            text: page.importNote
        }

        Kirigami.InlineMessage {
            Layout.fillWidth: true
            visible: serversModel.count === 0
            type: Kirigami.MessageType.Information
            text: i18n("No servers yet. Click “Add local” for the Docker on this machine, add one manually, or import your SSH hosts from Remmina or FileZilla (SFTP sites only). Imported password servers reuse the saved password from your keyring — no copy-paste. For manual servers, click “Set password” — it is stored securely in your KWallet/keyring, never in a file.")
        }

        Repeater {
            model: serversModel
            delegate: QQC2.Frame {
                id: rowFrame
                Layout.fillWidth: true
                property string rowAuth: model.auth
                property bool rowHasSecret: model.hasSecret || false

                GridLayout {
                    anchors.fill: parent
                    columns: 2
                    columnSpacing: Kirigami.Units.smallSpacing
                    rowSpacing: Kirigami.Units.smallSpacing / 2

                    QQC2.Label { text: i18n("Label") }
                    QQC2.TextField {
                        Layout.fillWidth: true
                        text: model.label
                        placeholderText: i18n("e.g. Production / web-01")
                        onEditingFinished: { serversModel.setProperty(index, "label", text); page.save(); }
                    }

                    QQC2.Label { text: i18n("user@host") }
                    QQC2.TextField {
                        Layout.fillWidth: true
                        text: (model.user ? model.user + "@" : "") + model.host
                        placeholderText: i18n("root@example.com")
                        onEditingFinished: {
                            var t = text.trim(), u = "", h = t;
                            var at = t.indexOf("@");
                            if (at >= 0) { u = t.substring(0, at); h = t.substring(at + 1); }
                            var col = h.indexOf(":");
                            if (col >= 0) {
                                var pp = parseInt(h.substring(col + 1));
                                if (!isNaN(pp)) serversModel.setProperty(index, "port", pp);
                                h = h.substring(0, col);
                            }
                            serversModel.setProperty(index, "user", u);
                            serversModel.setProperty(index, "host", h);
                            page.save();
                        }
                    }

                    QQC2.Label { text: i18n("Port") }
                    RowLayout {
                        Layout.fillWidth: true
                        QQC2.SpinBox {
                            from: 1; to: 65535
                            value: model.port
                            onValueModified: { serversModel.setProperty(index, "port", value); page.save(); }
                        }
                        Item { Layout.fillWidth: true }
                        QQC2.ToolButton {
                            icon.name: "list-remove"
                            text: i18n("Remove")
                            display: QQC2.AbstractButton.TextBesideIcon
                            onClicked: { serversModel.remove(index); page.save(); }
                        }
                        QQC2.ToolButton {
                            icon.name: "view-barcode-qr"
                            text: i18n("QR")
                            display: QQC2.AbstractButton.TextBesideIcon
                            onClicked: qrExporter.show(index)
                        }
                    }

                    QQC2.Label { text: i18n("Auth") }
                    RowLayout {
                        Layout.fillWidth: true
                        spacing: Kirigami.Units.smallSpacing
                        QQC2.ComboBox {
                            id: authCombo
                            textRole: "label"
                            valueRole: "key"
                            model: [
                                { key: "password", label: i18n("Password") },
                                { key: "key",      label: i18n("SSH key / agent") }
                            ]
                            currentIndex: rowFrame.rowAuth === "key" ? 1 : 0
                            onActivated: { serversModel.setProperty(index, "auth", currentValue); rowFrame.rowAuth = currentValue; page.save(); }
                        }
                        RowLayout {
                            visible: rowFrame.rowAuth === "password" && rowFrame.rowHasSecret
                            spacing: Kirigami.Units.smallSpacing / 2
                            Kirigami.Icon {
                                source: "emblem-success"
                                Layout.preferredWidth: Kirigami.Units.iconSizes.small
                                Layout.preferredHeight: Kirigami.Units.iconSizes.small
                            }
                            QQC2.Label {
                                text: i18n("Using Remmina password")
                                color: Kirigami.Theme.positiveTextColor
                                QQC2.ToolTip.visible: hov.hovered
                                QQC2.ToolTip.text: i18n("Reuses the password Remmina already saved in your keyring — nothing to copy. Set a password below only to override it.")
                                HoverHandler { id: hov }
                            }
                        }
                        QQC2.Button {
                            visible: rowFrame.rowAuth === "password"
                            text: rowFrame.rowHasSecret ? i18n("Override password") : i18n("Set password")
                            flat: rowFrame.rowHasSecret
                            icon.name: "lock"
                            onClicked: page.setPassword(model.label, model.user, model.host, model.port)
                        }
                        QQC2.ToolButton {
                            visible: rowFrame.rowAuth === "password"
                            icon.name: "edit-clear"
                            text: i18n("Clear")
                            display: QQC2.AbstractButton.TextBesideIcon
                            onClicked: page.clearPassword(model.user, model.host, model.port)
                        }
                        Item { Layout.fillWidth: true }
                    }

                    QQC2.Label {
                        text: i18n("Key file")
                        visible: rowFrame.rowAuth === "key"
                    }
                    QQC2.TextField {
                        Layout.fillWidth: true
                        visible: rowFrame.rowAuth === "key"
                        text: model.key
                        placeholderText: i18n("~/.ssh/id_ed25519 — leave empty to use ssh-agent")
                        onEditingFinished: { serversModel.setProperty(index, "key", text); page.save(); }
                    }

                    QQC2.Label { text: i18n("Privileges") }
                    ColumnLayout {
                        Layout.fillWidth: true
                        spacing: 0
                        QQC2.CheckBox {
                            text: i18n("Use sudo for nginx, certbot & config edits")
                            checked: model.useSudo || false
                            onToggled: { serversModel.setProperty(index, "useSudo", checked); page.save(); }
                        }
                        QQC2.Label {
                            Layout.fillWidth: true; wrapMode: Text.WordWrap
                            font: Kirigami.Theme.smallFont; opacity: 0.7
                            text: i18n("Runs privileged commands via sudo -n. Needs NOPASSWD sudo on the server. Leave off if the SSH user is root.")
                        }
                    }
                }
            }
        }
    }

    QQC2.Dialog {
        id: qrDialog
        parent: QQC2.Overlay.overlay
        modal: true
        title: i18n("Import into Dockswain Mobile")
        standardButtons: QQC2.Dialog.Close
        width: Math.min(page.width - Kirigami.Units.gridUnit * 2, Kirigami.Units.gridUnit * 36)
        x: (page.width - width) / 2
        y: Kirigami.Units.gridUnit

        ColumnLayout {
            width: parent.width
            spacing: Kirigami.Units.smallSpacing

            QQC2.Label {
                Layout.fillWidth: true
                text: page.qrSummary
                wrapMode: Text.WordWrap
            }

            Prison.Barcode {
                Layout.alignment: Qt.AlignHCenter
                Layout.preferredWidth: Math.min(qrDialog.width - Kirigami.Units.gridUnit * 2, Kirigami.Units.gridUnit * 30)
                Layout.preferredHeight: Math.min(qrDialog.width - Kirigami.Units.gridUnit * 2, Kirigami.Units.gridUnit * 30)
                barcodeType: Prison.Barcode.QRCode
                content: page.qrPayload
                foregroundColor: Kirigami.Theme.textColor
                backgroundColor: Kirigami.Theme.backgroundColor
            }

            Kirigami.InlineMessage {
                Layout.fillWidth: true
                visible: page.qrWarnings !== ""
                type: Kirigami.MessageType.Warning
                text: page.qrWarnings
            }

            QQC2.Label {
                Layout.fillWidth: true
                text: i18n("Open Dockswain Mobile → Settings → Scan QR, then point the camera at this code.")
                wrapMode: Text.WordWrap
                font: Kirigami.Theme.smallFont
                opacity: 0.75
            }

            QQC2.TextArea {
                id: qrText
                Layout.fillWidth: true
                Layout.preferredHeight: Kirigami.Units.gridUnit * 3
                text: page.qrPayload
                readOnly: true
                wrapMode: Text.WrapAnywhere
                font: Kirigami.Theme.smallFont
            }
        }
    }
}
