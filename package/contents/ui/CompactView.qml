import QtQuick
import QtQuick.Layouts
import org.kde.plasma.plasmoid
import org.kde.plasma.core as PlasmaCore
import org.kde.plasma.components as PlasmaComponents
import org.kde.kirigami as Kirigami

// Panel/compact view: fleet-wide status, not merely the selected server tab.
MouseArea {
    id: ca

    property int hostCount: 0
    property int observedCount: 0
    property int onlineCount: 0
    property int healthyCount: 0
    property int warningCount: 0
    property int criticalCount: 0
    property url iconSource

    signal toggleRequested()

    readonly property bool horizontal: Plasmoid.formFactor === PlasmaCore.Types.Horizontal
    readonly property int side: Math.min(width, height)

    hoverEnabled: true
    onClicked: ca.toggleRequested()

    Layout.minimumWidth: horizontal ? (row.implicitWidth) : Kirigami.Units.iconSizes.small
    Layout.preferredWidth: horizontal ? row.implicitWidth : height

    RowLayout {
        id: row
        anchors.fill: parent
        spacing: Kirigami.Units.smallSpacing

        Item {
            Layout.fillHeight: true
            Layout.preferredWidth: height
            Layout.alignment: Qt.AlignCenter

            Image {
                id: icon
                anchors.fill: parent
                source: ca.iconSource
                sourceSize.width: 128
                sourceSize.height: 128
                fillMode: Image.PreserveAspectFit
                smooth: true
                opacity: ca.hostCount === 0 ? 0.55 : 1.0
            }

            // reachability dot, bottom-right
            Rectangle {
                width: Math.max(6, parent.width * 0.26)
                height: width
                radius: width / 2
                anchors.right: parent.right
                anchors.bottom: parent.bottom
                color: ca.criticalCount > 0 ? Kirigami.Theme.negativeTextColor
                     : ca.warningCount > 0 ? Kirigami.Theme.neutralTextColor
                     : ca.observedCount < ca.hostCount ? Kirigami.Theme.disabledTextColor
                     : ca.hostCount > 0 && ca.onlineCount === ca.hostCount
                       ? Kirigami.Theme.positiveTextColor
                       : Kirigami.Theme.textColor
                border.width: Math.max(1, width * 0.12)
                border.color: Kirigami.Theme.backgroundColor
            }
        }

        PlasmaComponents.Label {
            visible: ca.horizontal && ca.hostCount > 0 && ca.width > Kirigami.Units.gridUnit * 3.5
            text: ca.criticalCount > 0 ? ("!" + ca.criticalCount)
                : ca.warningCount > 0 ? ("⚠" + ca.warningCount)
                : ca.observedCount < ca.hostCount ? (ca.observedCount + "/" + ca.hostCount + "…")
                : ca.onlineCount + "/" + ca.hostCount
            font.bold: true
            font.family: "monospace"
            color: ca.criticalCount > 0 ? Kirigami.Theme.negativeTextColor
                 : ca.warningCount > 0 ? Kirigami.Theme.neutralTextColor
                 : ca.observedCount < ca.hostCount ? Kirigami.Theme.disabledTextColor
                 : Kirigami.Theme.positiveTextColor
            Layout.alignment: Qt.AlignVCenter
        }
    }
}
