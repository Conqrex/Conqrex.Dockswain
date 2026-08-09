import QtQuick
import QtQuick.Layouts
import QtQuick.Controls as QQC2
import org.kde.plasma.components as PlasmaComponents
import org.kde.kirigami as Kirigami

// One docker-compose project card: status + name + files + immediate actions.
Rectangle {
    id: cd

    property string pname: model.pname
    property string pstatus: model.pstatus
    property string pstate: model.pstate
    property string pfiles: model.pfiles
    property bool pswarm: model.pswarm || false
    readonly property var filesArr: (pfiles && pfiles.length) ? pfiles.split(",") : []

    signal upRequested(string files, string name)
    signal downRequested(string files, string name)
    signal viewFileRequested(string path)
    signal editFileRequested(string path)
    signal stackRemoveRequested(string name)

    function baseName(p) { var i = p.lastIndexOf("/"); return i >= 0 ? p.substring(i + 1) : p; }

    readonly property bool up: pstate === "running"
    implicitHeight: projectRow.implicitHeight + Kirigami.Units.smallSpacing
    radius: Kirigami.Units.cornerRadius
    color: composeHover.hovered
        ? Qt.alpha(Kirigami.Theme.highlightColor, 0.10)
        : Qt.alpha(Kirigami.Theme.textColor, 0.035)
    HoverHandler { id: composeHover }

    RowLayout {
        id: projectRow
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.verticalCenter: parent.verticalCenter
        anchors.leftMargin: Kirigami.Units.smallSpacing
        anchors.rightMargin: Kirigami.Units.smallSpacing
        spacing: Kirigami.Units.smallSpacing

        Rectangle {
            Layout.preferredWidth: Kirigami.Units.iconSizes.small * 0.55
            Layout.preferredHeight: width
            radius: width / 2
            color: cd.up ? Kirigami.Theme.positiveTextColor
                 : cd.pstate === "exited" ? Kirigami.Theme.negativeTextColor
                 : Kirigami.Theme.neutralTextColor
        }
        PlasmaComponents.Label {
            text: cd.pname
            font.bold: true
            elide: Text.ElideRight
            Layout.fillWidth: true
        }
        PlasmaComponents.Label {
            text: cd.pstatus
            opacity: 0.6
            font: Kirigami.Theme.smallFont
        }

        PlasmaComponents.ToolButton {
            visible: !cd.pswarm                         // swarm stacks have no local compose file
            icon.name: "document-multiple"
            enabled: cd.filesArr.length > 0
            onClicked: cd.filesArr.length === 1 ? cd.viewFileRequested(cd.filesArr[0]) : filesMenu.open()
            PlasmaComponents.ToolTip { text: i18n("Compose file(s)") }

            QQC2.Menu {
                id: filesMenu
                Instantiator {
                    model: cd.filesArr
                    delegate: QQC2.MenuItem {
                        required property string modelData
                        text: i18n("View %1", cd.baseName(modelData))
                        onTriggered: cd.viewFileRequested(modelData)
                    }
                    onObjectAdded: (index, object) => filesMenu.insertItem(index, object)
                    onObjectRemoved: (index, object) => filesMenu.removeItem(object)
                }
            }
        }

        PlasmaComponents.ToolButton {
            visible: !cd.pswarm                         // swarm stacks are already deployed
            icon.name: "media-playback-start"
            enabled: !cd.up
            onClicked: cd.upRequested(cd.pfiles, cd.pname)
            PlasmaComponents.ToolTip { text: i18n("Up (start project)") }
        }
        PlasmaComponents.ToolButton {
            icon.name: cd.pswarm ? "edit-delete" : "media-playback-stop"
            enabled: cd.up || cd.pswarm
            onClicked: cd.pswarm ? cd.stackRemoveRequested(cd.pname) : cd.downRequested(cd.pfiles, cd.pname)
            PlasmaComponents.ToolTip { text: cd.pswarm ? i18n("Remove stack (docker stack rm)") : i18n("Down (stop project)") }
        }
    }
}
