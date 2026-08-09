import QtQuick
import QtQuick.Layouts
import QtQuick.Controls as QQC2
import org.kde.kcmutils as KCM
import org.kde.kirigami as Kirigami

KCM.SimpleKCM {
    id: page

    property alias cfg_pollInterval: pollSpin.value
    property alias cfg_statsInterval: statsSpin.value
    property alias cfg_showStats: statsBox.checked
    property alias cfg_useSystemTheme: sysThemeBox.checked
    property alias cfg_showCompose: composeBox.checked
    property alias cfg_confirmDestructive: confirmBox.checked
    property alias cfg_sshConnectTimeout: timeoutSpin.value
    property alias cfg_timeFormat24h: time24Box.checked
    property alias cfg_hideExitedDefault: hideExitedBox.checked
    property alias cfg_groupByNetwork: groupNetBox.checked
    property alias cfg_logTail: logTailSpin.value
    property alias cfg_logFollowInterval: logFollowSpin.value
    property alias cfg_nginxDir: nginxDirField.text
    property alias cfg_editor: editorField.text
    property alias cfg_defaultLocalDir: localDirField.text
    property alias cfg_fmPopupWidth: fmWidthSpin.value
    property alias cfg_fmPopupHeight: fmHeightSpin.value
    property alias cfg_confirmDelete: confirmDeleteBox.checked
    property alias cfg_showHiddenFiles: hiddenFilesBox.checked
    property alias cfg_fleetDefaultView: fleetDefaultBox.checked
    property alias cfg_fleetPollInterval: fleetPollSpin.value
    property alias cfg_fleetDeepInterval: fleetDeepSpin.value
    property alias cfg_fleetResourceMonitoring: resourceMonitorBox.checked
    property alias cfg_fleetDiskMonitoring: diskMonitorBox.checked
    property alias cfg_fleetSslMonitoring: sslMonitorBox.checked
    property alias cfg_fleetImageMonitoring: imageMonitorBox.checked
    property alias cfg_fleetNotifications: notificationsBox.checked
    property alias cfg_fleetNotifyRecovery: recoveryBox.checked
    property alias cfg_fleetCpuThreshold: cpuThresholdSpin.value
    property alias cfg_fleetMemoryThreshold: memoryThresholdSpin.value
    property alias cfg_fleetDiskThreshold: diskThresholdSpin.value
    property alias cfg_fleetSslWarningDays: sslDaysSpin.value
    property alias cfg_fleetRestartThreshold: restartThresholdSpin.value
    property alias cfg_fleetRestartWindowMinutes: restartWindowSpin.value
    property alias cfg_fleetEventHistoryLimit: historyLimitSpin.value

    property string cfg_dockerCmd: "docker"
    property string cfg_terminal: "konsole"
    property string cfg_sftpTool: "auto"

    Kirigami.FormLayout {

        QQC2.ComboBox {
            id: dockerCombo
            Kirigami.FormData.label: i18n("Docker command:")
            textRole: "label"
            valueRole: "key"
            model: [
                { key: "docker",      label: i18n("docker") },
                { key: "sudo docker", label: i18n("sudo docker") }
            ]
            currentIndex: Math.max(0, indexOfValue(page.cfg_dockerCmd))
            onActivated: page.cfg_dockerCmd = currentValue
        }
        QQC2.ComboBox {
            id: terminalCombo
            Kirigami.FormData.label: i18n("Terminal:")
            editable: true
            model: ["konsole", "alacritty"]
            Component.onCompleted: editText = page.cfg_terminal
            onEditTextChanged: page.cfg_terminal = editText
        }
        QQC2.CheckBox {
            id: sysThemeBox
            Kirigami.FormData.label: i18n("Colors:")
            text: i18n("Use Plasma system colors")
        }

        Item { Kirigami.FormData.isSection: true }

        QQC2.SpinBox {
            id: pollSpin
            from: 5; to: 600; stepSize: 5
            Kirigami.FormData.label: i18n("Refresh interval (s):")
        }
        QQC2.CheckBox {
            id: statsBox
            text: i18n("Show CPU / memory stats")
        }
        QQC2.SpinBox {
            id: statsSpin
            from: 15; to: 600; stepSize: 5
            enabled: statsBox.checked
            Kirigami.FormData.label: i18n("Stats interval (s):")
        }
        QQC2.SpinBox {
            id: timeoutSpin
            from: 2; to: 60
            Kirigami.FormData.label: i18n("SSH connect timeout (s):")
        }

        Item {
            Kirigami.FormData.isSection: true
            Kirigami.FormData.label: i18n("Fleet Health")
        }
        QQC2.CheckBox {
            id: fleetDefaultBox
            text: i18n("Open Fleet Health by default")
        }
        QQC2.SpinBox {
            id: fleetPollSpin
            from: 10; to: 600; stepSize: 5
            Kirigami.FormData.label: i18n("Health refresh (s):")
        }
        QQC2.SpinBox {
            id: fleetDeepSpin
            from: 300; to: 21600; stepSize: 300
            Kirigami.FormData.label: i18n("Disk / SSL refresh (s):")
        }
        QQC2.CheckBox {
            id: resourceMonitorBox
            text: i18n("Monitor CPU and memory thresholds")
        }
        QQC2.SpinBox {
            id: cpuThresholdSpin
            from: 1; to: 1000; suffix: "%"
            enabled: resourceMonitorBox.checked
            Kirigami.FormData.label: i18n("Container CPU warning:")
        }
        QQC2.Label {
            text: i18n("Docker CPU is measured per core: 100% is one full core, 200% is two.")
            wrapMode: Text.WordWrap
            opacity: 0.7
            font: Kirigami.Theme.smallFont
            enabled: resourceMonitorBox.checked
        }
        QQC2.SpinBox {
            id: memoryThresholdSpin
            from: 1; to: 100; suffix: "%"
            enabled: resourceMonitorBox.checked
            Kirigami.FormData.label: i18n("Memory warning:")
        }
        QQC2.CheckBox {
            id: diskMonitorBox
            text: i18n("Monitor Docker host disk pressure")
        }
        QQC2.SpinBox {
            id: diskThresholdSpin
            from: 1; to: 100; suffix: "%"
            enabled: diskMonitorBox.checked
            Kirigami.FormData.label: i18n("Disk warning:")
        }
        QQC2.CheckBox {
            id: sslMonitorBox
            text: i18n("Monitor certbot certificate expiry")
        }
        QQC2.SpinBox {
            id: sslDaysSpin
            from: 1; to: 180; suffix: i18n(" days")
            enabled: sslMonitorBox.checked
            Kirigami.FormData.label: i18n("SSL warning window:")
        }
        QQC2.CheckBox {
            id: imageMonitorBox
            text: i18n("Detect when a running container uses an older local image")
        }
        QQC2.SpinBox {
            id: restartThresholdSpin
            from: 1; to: 100
            Kirigami.FormData.label: i18n("Restart warning count:")
        }
        QQC2.SpinBox {
            id: restartWindowSpin
            from: 5; to: 1440; stepSize: 5; suffix: i18n(" min")
            Kirigami.FormData.label: i18n("Restart window:")
        }
        QQC2.CheckBox {
            id: notificationsBox
            text: i18n("Show KDE notifications for new warnings and failures")
        }
        QQC2.CheckBox {
            id: recoveryBox
            text: i18n("Also notify when problems recover")
            enabled: notificationsBox.checked
        }
        QQC2.Label {
            text: i18n("Choose alert types and project/container filters for each host on the Servers page.")
            wrapMode: Text.WordWrap
            opacity: 0.7
            font: Kirigami.Theme.smallFont
            enabled: notificationsBox.checked
        }
        QQC2.SpinBox {
            id: historyLimitSpin
            from: 50; to: 1000; stepSize: 50
            Kirigami.FormData.label: i18n("Event history limit:")
        }

        Item { Kirigami.FormData.isSection: true }

        QQC2.CheckBox {
            id: composeBox
            text: i18n("Show compose projects")
        }
        QQC2.CheckBox {
            id: confirmBox
            text: i18n("Confirm destructive actions (remove, compose down)")
        }
        QQC2.CheckBox {
            id: time24Box
            text: i18n("Use 24-hour time")
        }
        QQC2.CheckBox {
            id: hideExitedBox
            text: i18n("Hide exited containers by default")
        }
        QQC2.CheckBox {
            id: groupNetBox
            text: i18n("Group containers by docker network")
        }

        Item { Kirigami.FormData.isSection: true }

        QQC2.SpinBox {
            id: logTailSpin
            from: 50; to: 5000; stepSize: 50
            Kirigami.FormData.label: i18n("Log lines (tail):")
        }
        QQC2.SpinBox {
            id: logFollowSpin
            from: 1; to: 30
            Kirigami.FormData.label: i18n("Log follow interval (s):")
        }

        Item { Kirigami.FormData.isSection: true }

        QQC2.TextField {
            id: nginxDirField
            Kirigami.FormData.label: i18n("nginx directory:")
            placeholderText: "/etc/nginx"
            Layout.minimumWidth: Kirigami.Units.gridUnit * 14
        }
        QQC2.TextField {
            id: editorField
            Kirigami.FormData.label: i18n("Editor:")
            placeholderText: "kate"
            Layout.minimumWidth: Kirigami.Units.gridUnit * 14
        }

        Item {
            Kirigami.FormData.isSection: true
            Kirigami.FormData.label: i18n("File manager")
        }

        QQC2.TextField {
            id: localDirField
            Kirigami.FormData.label: i18n("Open local at:")
            placeholderText: i18n("(home)")
            Layout.minimumWidth: Kirigami.Units.gridUnit * 14
        }
        QQC2.ComboBox {
            id: sftpToolCombo
            Kirigami.FormData.label: i18n("Transfer tool:")
            textRole: "label"
            valueRole: "key"
            model: [
                { key: "auto",  label: i18n("auto (rsync if available, else scp)") },
                { key: "rsync", label: i18n("rsync (live progress + sync)") },
                { key: "scp",   label: i18n("scp (always available)") }
            ]
            currentIndex: Math.max(0, indexOfValue(page.cfg_sftpTool))
            onActivated: page.cfg_sftpTool = currentValue
        }
        QQC2.SpinBox {
            id: fmWidthSpin
            from: 24; to: 80
            Kirigami.FormData.label: i18n("Popup width (grid units):")
        }
        QQC2.SpinBox {
            id: fmHeightSpin
            from: 20; to: 60
            Kirigami.FormData.label: i18n("Popup height (grid units):")
        }
        QQC2.CheckBox {
            id: confirmDeleteBox
            text: i18n("Confirm file deletes")
        }
        QQC2.CheckBox {
            id: hiddenFilesBox
            text: i18n("Show hidden files (dotfiles)")
        }
    }
}
