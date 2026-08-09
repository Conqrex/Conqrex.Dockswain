import QtQuick
import QtQuick.Layouts
import QtQuick.Controls as QQC2
import org.kde.plasma.plasmoid
import org.kde.plasma.components as PlasmaComponents
import org.kde.plasma.extras as PlasmaExtras
import org.kde.kirigami as Kirigami
import "../code/format.js" as Fmt

Item {
    id: view
    property var ctrl
    property int currentSection: 0

    function severityColor(severity) {
        return severity === "critical" ? Kirigami.Theme.negativeTextColor
             : severity === "warning" ? Kirigami.Theme.neutralTextColor
             : severity === "ok" ? Kirigami.Theme.positiveTextColor
             : Kirigami.Theme.textColor;
    }
    function openContainers() {
        if (ctrl.openTabs.length > 0) ctrl.setActiveTab(ctrl.activeTab);
        else if (ctrl.servers.length > 0) ctrl.openFleetServer(0, "");
    }
    function hostColor(observed, reasonCode, reachable, dockerOk) {
        if (!observed) return Kirigami.Theme.disabledTextColor;
        if (reasonCode === "no_password") return Kirigami.Theme.neutralTextColor;
        if (!reachable || !dockerOk) return Kirigami.Theme.negativeTextColor;
        return Kirigami.Theme.positiveTextColor;
    }

    ColumnLayout {
        anchors.fill: parent
        anchors.margins: Kirigami.Units.smallSpacing
        spacing: Kirigami.Units.smallSpacing

        RowLayout {
            Layout.fillWidth: true
            Image {
                source: ctrl.iconSource
                Layout.preferredWidth: Kirigami.Units.iconSizes.smallMedium
                Layout.preferredHeight: Kirigami.Units.iconSizes.smallMedium
                sourceSize.width: 128; sourceSize.height: 128
            }
            ColumnLayout {
                Layout.fillWidth: true; spacing: 0
                PlasmaComponents.Label { text: i18n("Dockswain"); font.bold: true }
                PlasmaComponents.Label {
                    text: i18n("Fleet Health · all configured Docker hosts")
                    opacity: 0.58; font: Kirigami.Theme.smallFont
                }
            }
            Rectangle {
                radius: height / 2
                color: Qt.alpha(Kirigami.Theme.textColor, 0.07)
                implicitHeight: fleetStatusRow.implicitHeight + 6
                implicitWidth: fleetStatusRow.implicitWidth + 14
                RowLayout {
                    id: fleetStatusRow
                    anchors.centerIn: parent
                    spacing: 4
                    Rectangle {
                        width: 7; height: 7; radius: 3.5
                        color: ctrl.fleet.checkingCount > 0 ? Kirigami.Theme.highlightColor
                             : ctrl.fleet.onlineCount === ctrl.fleet.hostCount ? Kirigami.Theme.positiveTextColor
                             : Kirigami.Theme.neutralTextColor
                    }
                    PlasmaComponents.Label {
                        text: ctrl.fleet.checkingCount > 0
                            ? i18n("Checking %1", ctrl.fleet.checkingCount)
                            : i18n("%1/%2 online", ctrl.fleet.onlineCount, ctrl.fleet.hostCount)
                        font.pointSize: Kirigami.Theme.smallFont.pointSize
                    }
                }
            }
            PlasmaComponents.ToolButton {
                icon.name: "docker"; text: i18n("Containers")
                display: PlasmaComponents.AbstractButton.TextBesideIcon
                flat: true
                enabled: ctrl.servers.length > 0
                onClicked: view.openContainers()
                PlasmaComponents.ToolTip { text: i18n("Open the interactive container view") }
            }
            PlasmaComponents.ToolButton {
                icon.name: "view-refresh"
                onClicked: ctrl.refreshFleet()
                PlasmaComponents.ToolTip { text: i18n("Refresh every host") }
            }
            PlasmaComponents.ToolButton {
                id: fleetPinButton
                checkable: true; checked: ctrl.pinned
                icon.name: ctrl.pinned ? "window-unpin" : "window-pin"
                onToggled: ctrl.pinned = checked
                PlasmaComponents.ToolTip { text: fleetPinButton.checked ? i18n("Pinned open") : i18n("Pin open") }
            }
        }

        GridLayout {
            Layout.fillWidth: true
            columns: 4
            columnSpacing: Kirigami.Units.smallSpacing
            SummaryCard {
                title: ctrl.fleet.checkingCount > 0 ? i18n("Checked") : i18n("Online")
                value: (ctrl.fleet.checkingCount > 0 ? ctrl.fleet.observedCount : ctrl.fleet.onlineCount)
                       + "/" + ctrl.fleet.hostCount
                iconName: "network-server"
                tone: ctrl.fleet.checkingCount > 0 ? "normal"
                    : ctrl.fleet.onlineCount === ctrl.fleet.hostCount ? "ok" : "warning"
            }
            SummaryCard { title: i18n("Healthy"); value: "" + ctrl.fleet.healthyCount; iconName: "emblem-success"; tone: "ok" }
            SummaryCard { title: i18n("Warnings"); value: "" + ctrl.fleet.warningProblemCount; iconName: "dialog-warning-symbolic"; tone: "warning" }
            SummaryCard { title: i18n("Critical"); value: "" + ctrl.fleet.criticalProblemCount; iconName: "dialog-error-symbolic"; tone: "critical" }
        }

        RowLayout {
            Layout.fillWidth: true
            visible: ctrl.fleet.checkingCount > 0
            spacing: Kirigami.Units.smallSpacing
            QQC2.BusyIndicator {
                running: parent.visible
                Layout.preferredWidth: Kirigami.Units.iconSizes.small
                Layout.preferredHeight: width
            }
            PlasmaComponents.Label {
                Layout.fillWidth: true
                text: i18np("Collecting status from %1 host…", "Collecting status from %1 hosts…", ctrl.fleet.checkingCount)
                opacity: 0.65; font: Kirigami.Theme.smallFont
            }
        }

        RowLayout {
            id: sectionTabs
            Layout.fillWidth: true
            spacing: Kirigami.Units.smallSpacing
            Repeater {
                model: [
                    { label: i18n("Problems"), icon: "dialog-warning-symbolic", count: ctrl.fleet.problemCount },
                    { label: i18n("Hosts"), icon: "network-server", count: ctrl.fleet.hostCount },
                    { label: i18n("SSL"), icon: "security-high", count: ctrl.fleet.expiringCertCount },
                    { label: i18n("Images"), icon: "docker", count: ctrl.fleet.imageUpdateCount },
                    { label: i18n("History"), icon: "view-history", count: ctrl.fleet.eventModel.count }
                ]
                delegate: Rectangle {
                    id: sectionChip
                    required property var modelData
                    required property int index
                    Layout.fillWidth: true
                    implicitHeight: sectionChipRow.implicitHeight + Kirigami.Units.smallSpacing
                    radius: height / 2
                    color: view.currentSection === index
                        ? Qt.alpha(Kirigami.Theme.highlightColor, 0.22)
                        : sectionHover.hovered ? Qt.alpha(Kirigami.Theme.textColor, 0.08)
                                               : Qt.alpha(Kirigami.Theme.textColor, 0.045)
                    border.width: view.currentSection === index ? 1 : 0
                    border.color: Qt.alpha(Kirigami.Theme.highlightColor, 0.60)

                    HoverHandler { id: sectionHover }
                    TapHandler { onTapped: view.currentSection = sectionChip.index }
                    RowLayout {
                        id: sectionChipRow
                        anchors.centerIn: parent
                        spacing: 4
                        Kirigami.Icon {
                            source: sectionChip.modelData.icon
                            Layout.preferredWidth: Kirigami.Units.iconSizes.small
                            Layout.preferredHeight: Kirigami.Units.iconSizes.small
                            opacity: view.currentSection === sectionChip.index ? 1 : 0.70
                        }
                        PlasmaComponents.Label {
                            text: sectionChip.modelData.label
                            font.bold: view.currentSection === sectionChip.index
                            font.pointSize: Kirigami.Theme.smallFont.pointSize
                        }
                        Rectangle {
                            visible: sectionChip.modelData.count > 0
                            implicitHeight: sectionCount.implicitHeight + 2
                            implicitWidth: Math.max(implicitHeight, sectionCount.implicitWidth + 8)
                            radius: height / 2
                            color: Qt.alpha(view.currentSection === sectionChip.index
                                            ? Kirigami.Theme.highlightColor : Kirigami.Theme.textColor, 0.16)
                            PlasmaComponents.Label {
                                id: sectionCount
                                anchors.centerIn: parent
                                text: sectionChip.modelData.count
                                font.bold: true
                                font.pointSize: Kirigami.Theme.smallFont.pointSize
                            }
                        }
                    }
                }
            }
        }

        StackLayout {
            Layout.fillWidth: true
            Layout.fillHeight: true
            currentIndex: view.currentSection

            Item {
                PlasmaExtras.PlaceholderMessage {
                    anchors.centerIn: parent
                    visible: ctrl.servers.length === 0
                    iconName: "network-server"
                    text: i18n("No servers configured")
                    explanation: i18n("Add Docker hosts in Dockswain settings.")
                }
                PlasmaExtras.PlaceholderMessage {
                    anchors.centerIn: parent
                    visible: ctrl.servers.length > 0 && ctrl.fleet.checkingCount > 0
                             && ctrl.fleet.problemCount === 0
                    iconName: "view-refresh"
                    text: i18n("Collecting fleet status")
                    explanation: i18np("Waiting for %1 host to respond", "Waiting for %1 hosts to respond", ctrl.fleet.checkingCount)
                }
                PlasmaExtras.PlaceholderMessage {
                    anchors.centerIn: parent
                    visible: ctrl.servers.length > 0 && ctrl.fleet.checkingCount === 0
                             && ctrl.fleet.problemCount === 0
                    iconName: "emblem-success"
                    text: i18n("Fleet looks healthy")
                    explanation: i18n("No current operational problems were detected.")
                }
                PlasmaComponents.ScrollView {
                    anchors.fill: parent
                    visible: ctrl.fleet.problemCount > 0
                    ListView {
                        model: ctrl.fleet.problemModel
                        clip: true; spacing: Kirigami.Units.smallSpacing
                        boundsBehavior: Flickable.StopAtBounds
                        delegate: QQC2.Frame {
                            width: ListView.view ? ListView.view.width : 0
                            background: Rectangle {
                                radius: Kirigami.Units.cornerRadius
                                color: problemHover.hovered
                                    ? Qt.alpha(Kirigami.Theme.highlightColor, 0.10)
                                    : Qt.alpha(Kirigami.Theme.textColor, 0.045)
                                border.width: 1
                                border.color: Qt.alpha(view.severityColor(model.severity), 0.58)
                            }
                            HoverHandler { id: problemHover }
                            contentItem: RowLayout {
                                spacing: Kirigami.Units.smallSpacing
                                StatusRing { accent: view.severityColor(model.severity) }
                                ColumnLayout {
                                    Layout.fillWidth: true; spacing: 0
                                    PlasmaComponents.Label {
                                        Layout.fillWidth: true; elide: Text.ElideRight
                                        text: model.title; font.bold: true
                                    }
                                    PlasmaComponents.Label {
                                        Layout.fillWidth: true; elide: Text.ElideRight
                                        text: model.serverLabel + (model.containerName ? " / " + model.containerName : "")
                                        opacity: 0.72; font: Kirigami.Theme.smallFont
                                    }
                                    PlasmaComponents.Label {
                                        Layout.fillWidth: true; elide: Text.ElideRight
                                        visible: model.detail !== ""; text: model.detail
                                        opacity: 0.62; font: Kirigami.Theme.smallFont
                                    }
                                }
                                QQC2.Button {
                                    visible: model.restartable && model.containerId !== ""
                                    text: i18n("Restart"); icon.name: "view-refresh"
                                    onClicked: ctrl.restartFleetContainer(model.serverIndex, model.containerId)
                                }
                                QQC2.Button {
                                    text: model.kind === "credentials" ? i18n("Settings")
                                        : model.feature === "disk" ? i18n("Cleanup")
                                        : model.feature === "nginx" ? i18n("Certificates") : i18n("Open")
                                    icon.name: model.kind === "credentials" ? "configure"
                                             : model.feature === "disk" ? "drive-harddisk"
                                             : model.feature === "nginx" ? "security-high" : "go-next"
                                    enabled: model.serverIndex >= 0
                                    onClicked: {
                                        if (model.kind === "credentials") ctrl.openSettings();
                                        else ctrl.openFleetServer(model.serverIndex, model.feature);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            Item {
                PlasmaComponents.ScrollView {
                    anchors.fill: parent
                    ListView {
                        model: ctrl.fleet.hostModel
                        clip: true; spacing: Kirigami.Units.smallSpacing
                        delegate: QQC2.Frame {
                            width: ListView.view ? ListView.view.width : 0
                            background: Rectangle {
                                radius: Kirigami.Units.cornerRadius
                                color: hostHover.hovered
                                    ? Qt.alpha(Kirigami.Theme.highlightColor, 0.10)
                                    : Qt.alpha(Kirigami.Theme.textColor, 0.045)
                                border.width: model.observed && (!model.reachable || !model.dockerOk) ? 1 : 0
                                border.color: Qt.alpha(view.hostColor(model.observed, model.reasonCode,
                                                                     model.reachable, model.dockerOk), 0.58)
                            }
                            HoverHandler { id: hostHover }
                            contentItem: RowLayout {
                                StatusRing {
                                    accent: view.hostColor(model.observed, model.reasonCode,
                                                           model.reachable, model.dockerOk)
                                }
                                ColumnLayout {
                                    Layout.fillWidth: true; spacing: 0
                                    PlasmaComponents.Label {
                                        Layout.fillWidth: true
                                        text: model.label; font.bold: true; elide: Text.ElideRight
                                    }
                                    PlasmaComponents.Label {
                                        Layout.fillWidth: true; elide: Text.ElideRight
                                        text: !model.observed ? i18n("Collecting status…")
                                            : !model.reachable ? (model.reason || i18n("SSH unavailable"))
                                            : !model.dockerOk ? i18n("SSH online · %1", model.reason || i18n("Docker unavailable"))
                                            : i18n("%1/%2 running · %3 healthy", model.running, model.total, model.healthy)
                                        opacity: 0.7; font: Kirigami.Theme.smallFont
                                    }
                                    PlasmaComponents.Label {
                                        Layout.fillWidth: true; elide: Text.ElideRight
                                        visible: model.observed && model.dockerOk
                                        text: (model.memoryPct > 0 ? i18n("memory %1%", Math.round(model.memoryPct)) : "")
                                            + (model.diskPct ? i18n(" · disk %1", model.diskPct) : "")
                                            + (model.sampledAt > 0 ? i18n(" · %1 ago", Fmt.ageText(model.sampledAt)) : "")
                                        opacity: 0.55; font: Kirigami.Theme.smallFont
                                    }
                                }
                                RowLayout {
                                    spacing: Kirigami.Units.smallSpacing
                                    Layout.alignment: Qt.AlignVCenter
                                    PlasmaComponents.ToolButton {
                                        icon.name: "view-refresh"
                                        onClicked: ctrl.refreshFleetHost(model.serverIndex)
                                        PlasmaComponents.ToolTip { text: i18n("Refresh host") }
                                    }
                                    QQC2.Button {
                                        Layout.preferredWidth: Kirigami.Units.gridUnit * 4.8
                                        text: model.reasonCode === "no_password" ? i18n("Settings") : i18n("Open")
                                        icon.name: model.reasonCode === "no_password" ? "configure" : "go-next"
                                        onClicked: {
                                            if (model.reasonCode === "no_password") ctrl.openSettings();
                                            else ctrl.openFleetServer(model.serverIndex, "");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            Item {
                PlasmaExtras.PlaceholderMessage {
                    anchors.centerIn: parent
                    visible: ctrl.fleet.certificateModel.count === 0
                    iconName: "security-high"; text: i18n("No certificates reported")
                    explanation: i18n("Certbot may not be installed, or the first SSL check is still running.")
                }
                PlasmaComponents.ScrollView {
                    anchors.fill: parent; visible: ctrl.fleet.certificateModel.count > 0
                    ListView {
                        model: ctrl.fleet.certificateModel; clip: true; spacing: Kirigami.Units.smallSpacing
                        delegate: QQC2.Frame {
                            width: ListView.view ? ListView.view.width : 0
                            background: Rectangle {
                                radius: Kirigami.Units.cornerRadius
                                color: certHover.hovered
                                    ? Qt.alpha(Kirigami.Theme.highlightColor, 0.10)
                                    : Qt.alpha(Kirigami.Theme.textColor, 0.045)
                                border.width: model.days <= Plasmoid.configuration.fleetSslWarningDays ? 1 : 0
                                border.color: Qt.alpha(model.days < 0 ? Kirigami.Theme.negativeTextColor
                                                     : Kirigami.Theme.neutralTextColor, 0.58)
                            }
                            HoverHandler { id: certHover }
                            contentItem: RowLayout {
                                Kirigami.Icon {
                                    source: model.days < 0 ? "security-low" : model.days <= Plasmoid.configuration.fleetSslWarningDays ? "security-medium" : "security-high"
                                    color: model.days < 0 ? Kirigami.Theme.negativeTextColor
                                         : model.days <= Plasmoid.configuration.fleetSslWarningDays ? Kirigami.Theme.neutralTextColor
                                         : Kirigami.Theme.positiveTextColor
                                    Layout.preferredWidth: Kirigami.Units.iconSizes.medium
                                    Layout.preferredHeight: width
                                }
                                ColumnLayout {
                                    Layout.fillWidth: true; spacing: 0
                                    PlasmaComponents.Label { text: model.domains || model.name; font.family: "monospace"; elide: Text.ElideRight; Layout.fillWidth: true }
                                    PlasmaComponents.Label { text: model.serverLabel + " · " + model.expiry; opacity: 0.65; font: Kirigami.Theme.smallFont }
                                }
                                PlasmaComponents.Label {
                                    text: model.days < 0 ? i18np("expired %1d", "expired %1d", -model.days)
                                                         : i18np("%1 day", "%1 days", model.days)
                                    font.bold: true
                                    color: model.days < 0 ? Kirigami.Theme.negativeTextColor
                                         : model.days <= Plasmoid.configuration.fleetSslWarningDays ? Kirigami.Theme.neutralTextColor
                                         : Kirigami.Theme.positiveTextColor
                                }
                                QQC2.Button { text: i18n("Manage"); onClicked: ctrl.openFleetServer(model.serverIndex, "nginx") }
                            }
                        }
                    }
                }
            }

            Item {
                ColumnLayout {
                    anchors.fill: parent
                    PlasmaComponents.Label {
                        Layout.fillWidth: true; wrapMode: Text.WordWrap
                        text: i18n("Dockswain compares each running container's image ID with its current local tag. Checks are read-only: it never pulls images in the background. Digest-pinned references are shown separately.")
                        opacity: 0.65; font: Kirigami.Theme.smallFont
                    }
                    PlasmaComponents.ScrollView {
                        Layout.fillWidth: true; Layout.fillHeight: true
                        ListView {
                            model: ctrl.fleet.imageModel; clip: true; spacing: Kirigami.Units.smallSpacing
                            delegate: QQC2.Frame {
                                width: ListView.view ? ListView.view.width : 0
                                background: Rectangle {
                                    radius: Kirigami.Units.cornerRadius
                                    color: imageHover.hovered
                                        ? Qt.alpha(Kirigami.Theme.highlightColor, 0.10)
                                        : Qt.alpha(Kirigami.Theme.textColor, 0.045)
                                    border.width: model.updateAvailable ? 1 : 0
                                    border.color: Qt.alpha(Kirigami.Theme.neutralTextColor, 0.58)
                                }
                                HoverHandler { id: imageHover }
                                contentItem: RowLayout {
                                    Kirigami.Icon {
                                        source: model.updateAvailable ? "software-update-available" : model.pinned ? "object-locked" : "docker"
                                        color: model.updateAvailable ? Kirigami.Theme.neutralTextColor : Kirigami.Theme.textColor
                                        Layout.preferredWidth: Kirigami.Units.iconSizes.medium; Layout.preferredHeight: width
                                    }
                                    ColumnLayout {
                                        Layout.fillWidth: true; spacing: 0
                                        PlasmaComponents.Label { text: model.ref; font.family: "monospace"; Layout.fillWidth: true; elide: Text.ElideMiddle }
                                        PlasmaComponents.Label {
                                            text: model.serverLabel + " · " + (model.updateAvailable ? i18n("newer local image available")
                                                  : model.pinned ? i18n("digest pinned") : i18n("tag reference"))
                                            opacity: 0.65; font: Kirigami.Theme.smallFont
                                        }
                                    }
                                    QQC2.Button { text: i18n("Open"); onClicked: ctrl.openFleetServer(model.serverIndex, "containers") }
                                }
                            }
                        }
                    }
                }
            }

            Item {
                ColumnLayout {
                    anchors.fill: parent
                    RowLayout {
                        Layout.fillWidth: true
                        PlasmaComponents.Label {
                            Layout.fillWidth: true
                            text: i18n("Persistent event history · newest first")
                            opacity: 0.65; font: Kirigami.Theme.smallFont
                        }
                        QQC2.Button {
                            text: i18n("Clear"); icon.name: "edit-clear-history"
                            enabled: ctrl.fleet.eventModel.count > 0
                            onClicked: ctrl.clearFleetEvents()
                        }
                    }
                    PlasmaExtras.PlaceholderMessage {
                        Layout.fillWidth: true; Layout.fillHeight: true
                        visible: ctrl.fleet.eventModel.count === 0
                        iconName: "view-history"; text: i18n("No changes recorded yet")
                    }
                    PlasmaComponents.ScrollView {
                        Layout.fillWidth: true; Layout.fillHeight: true
                        visible: ctrl.fleet.eventModel.count > 0
                        ListView {
                            model: ctrl.fleet.eventModel; clip: true; spacing: Kirigami.Units.smallSpacing / 2
                            delegate: Rectangle {
                                width: ListView.view ? ListView.view.width : 0
                                implicitHeight: historyRow.implicitHeight + Kirigami.Units.smallSpacing
                                radius: Kirigami.Units.cornerRadius
                                color: historyHover.hovered
                                    ? Qt.alpha(Kirigami.Theme.highlightColor, 0.10)
                                    : Qt.alpha(Kirigami.Theme.textColor, 0.035)
                                HoverHandler { id: historyHover }
                                RowLayout {
                                    id: historyRow
                                    anchors.left: parent.left
                                    anchors.right: parent.right
                                    anchors.verticalCenter: parent.verticalCenter
                                    anchors.leftMargin: Kirigami.Units.smallSpacing
                                    anchors.rightMargin: Kirigami.Units.smallSpacing
                                    spacing: Kirigami.Units.smallSpacing
                                    PlasmaComponents.Label {
                                        text: Qt.formatDateTime(new Date(model.timestamp), Plasmoid.configuration.timeFormat24h ? "MM-dd HH:mm" : "MM-dd h:mm AP")
                                        font.family: "monospace"
                                        font.pixelSize: Kirigami.Theme.smallFont.pixelSize
                                        opacity: 0.52
                                    }
                                    Rectangle {
                                        Layout.preferredWidth: 7; Layout.preferredHeight: 7
                                        radius: 3.5; color: view.severityColor(model.severity)
                                    }
                                    ColumnLayout {
                                        Layout.fillWidth: true; spacing: 0
                                        PlasmaComponents.Label { text: model.title; Layout.fillWidth: true; elide: Text.ElideRight }
                                        PlasmaComponents.Label {
                                            text: model.serverLabel + (model.containerName ? " / " + model.containerName : "")
                                                + (model.detail ? " · " + model.detail : "")
                                            Layout.fillWidth: true; elide: Text.ElideRight
                                            opacity: 0.58; font: Kirigami.Theme.smallFont
                                        }
                                    }
                                    PlasmaComponents.ToolButton {
                                        visible: model.serverIndex >= 0; icon.name: "go-next"
                                        onClicked: ctrl.openFleetServer(model.serverIndex, "")
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    component SummaryCard: Rectangle {
        id: summary
        required property string title
        required property string value
        required property string iconName
        property string tone: "normal"
        Layout.fillWidth: true
        Layout.preferredHeight: Kirigami.Units.gridUnit * 3.0
        radius: Kirigami.Units.cornerRadius
        color: Qt.alpha(Kirigami.Theme.textColor, 0.045)
        border.width: 1; border.color: Qt.alpha(accent, 0.30)
        property color accent: tone === "critical" ? Kirigami.Theme.negativeTextColor
                             : tone === "warning" ? Kirigami.Theme.neutralTextColor
                             : tone === "ok" ? Kirigami.Theme.positiveTextColor
                             : Kirigami.Theme.highlightColor
        Rectangle {
            anchors.left: parent.left; anchors.top: parent.top; anchors.bottom: parent.bottom
            width: 2; radius: 1; color: parent.accent
        }
        RowLayout {
            anchors.centerIn: parent
            spacing: Kirigami.Units.smallSpacing
            Kirigami.Icon {
                source: summary.iconName
                color: summary.accent
                Layout.preferredWidth: Kirigami.Units.iconSizes.smallMedium
                Layout.preferredHeight: width
            }
            ColumnLayout {
                spacing: 0
                PlasmaComponents.Label { text: summary.value; font.bold: true; font.pointSize: Kirigami.Theme.defaultFont.pointSize * 1.25; color: summary.accent }
                PlasmaComponents.Label { text: summary.title.toUpperCase(); opacity: 0.58; font.pointSize: Kirigami.Theme.smallFont.pointSize; font.letterSpacing: 0.5 }
            }
        }
    }

    component StatusRing: Item {
        required property color accent
        Layout.preferredWidth: Kirigami.Units.iconSizes.medium
        Layout.preferredHeight: Kirigami.Units.iconSizes.medium
        Layout.alignment: Qt.AlignVCenter
        Rectangle {
            anchors.fill: parent
            radius: width / 2
            color: Qt.alpha(parent.accent, 0.12)
            border.width: 2
            border.color: parent.accent
        }
        Rectangle {
            anchors.centerIn: parent
            width: 7; height: 7; radius: 3.5
            color: parent.accent
        }
    }
}
