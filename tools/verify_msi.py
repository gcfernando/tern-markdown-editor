"""Read MSI tables without installing or changing a user's file associations."""
from pathlib import Path
import argparse
import ctypes as C
from ctypes import wintypes as W
import json


def verify(path: Path, payload: Path) -> dict:
    msi = C.WinDLL("msi")
    handle = W.UINT()
    msi.MsiOpenDatabaseW.argtypes = [W.LPCWSTR, W.LPCWSTR, C.POINTER(W.UINT)]
    msi.MsiDatabaseOpenViewW.argtypes = [W.UINT, W.LPCWSTR, C.POINTER(W.UINT)]
    msi.MsiViewExecute.argtypes = [W.UINT, W.UINT]
    msi.MsiViewFetch.argtypes = [W.UINT, C.POINTER(W.UINT)]
    msi.MsiRecordGetStringW.argtypes = [W.UINT, W.UINT, W.LPWSTR, C.POINTER(W.DWORD)]

    def check(code):
        if code:
            raise RuntimeError(f"Windows Installer query failed: {code}")

    check(msi.MsiOpenDatabaseW(str(path.resolve()), None, C.byref(handle)))

    def rows(sql, columns):
        view = W.UINT()
        check(msi.MsiDatabaseOpenViewW(handle, sql, C.byref(view)))
        try:
            check(msi.MsiViewExecute(view, 0))
            result = []
            while True:
                record = W.UINT()
                code = msi.MsiViewFetch(view, C.byref(record))
                if code == 259:
                    return result
                check(code)
                try:
                    values = []
                    for i in range(1, columns + 1):
                        count = W.DWORD(32768)
                        value = C.create_unicode_buffer(count.value)
                        check(msi.MsiRecordGetStringW(record, i, value, C.byref(count)))
                        values.append(value.value)
                    result.append(values)
                finally:
                    msi.MsiCloseHandle(record)
        finally:
            msi.MsiCloseHandle(view)

    try:
        properties = dict(rows("SELECT `Property`, `Value` FROM `Property`", 2))
        expected = json.loads((payload / "package-manifest.json").read_text())
        if properties.get("ProductVersion") != expected["version"]:
            raise RuntimeError("MSI version disagrees with portable payload")
        if properties.get("ALLUSERS"):
            raise RuntimeError("MSI must install for the current user")
        registry = rows("SELECT `Root`, `Key`, `Name`, `Value` FROM `Registry`", 4)
        if any(root != "1" or "userchoice" in key.lower() or key.lower() == r"software\classes\.md" for root, key, _, _ in registry):
            raise RuntimeError("Installer must not take the Markdown default or use machine-wide registry keys")
        command_key = r"Software\Classes\SystemFileAssociations\.md\shell\Tern.Open\command"
        commands = [value for _, key, _, value in registry if key == command_key]
        if commands != ['"[INSTALLFOLDER]Tern.exe" "%1"']:
            raise RuntimeError("Missing or incorrectly quoted Open with Tern action")
        if not any(key == r"Software\Classes\.md\OpenWithProgids" and name == "Tern.Markdown" for _, key, name, _ in registry):
            raise RuntimeError("Missing Markdown Open with registration")
        files = rows("SELECT `File`, `FileSize` FROM `File`", 2)
        if len(files) != len(expected["files"]) + 1:
            raise RuntimeError("MSI file count disagrees with complete portable payload")
        dialogs = {row[0] for row in rows("SELECT `Dialog` FROM `Dialog`", 1)}
        required_dialogs = {"WelcomeDlg", "LicenseAgreementDlg", "InstallDirDlg", "TernShortcutsDlg", "VerifyReadyDlg", "ProgressDlg", "ExitDialog"}
        if not required_dialogs.issubset(dialogs):
            raise RuntimeError("Installer wizard is incomplete")
        fonts = rows("SELECT `TextStyle`, `FaceName`, `Size` FROM `TextStyle`", 3)
        if not all([name, "Segoe UI", size] in fonts for name, size in
                   [("WixUI_Font_Normal", "9"), ("WixUI_Font_Title", "10"), ("WixUI_Font_Bigger", "16")]):
            raise RuntimeError("Installer must use the Tern Segoe UI typography")
        titles = rows("SELECT `Text` FROM `Control` WHERE `Dialog_`='LicenseAgreementDlg' AND `Control`='Title'", 1)
        if len(titles) != 1 or 'License agreement' not in titles[0][0]:
            raise RuntimeError("Installer license heading is not the current branded version")
        ui_sequence = {action: (condition, int(sequence)) for action, condition, sequence in
                       rows("SELECT `Action`, `Condition`, `Sequence` FROM `InstallUISequence`", 3)}
        if not all(action in ui_sequence for action in ("WelcomeDlg", "ProgressDlg", "ExecuteAction", "ExitDialog")) or not (
                ui_sequence["WelcomeDlg"][1] < ui_sequence["ProgressDlg"][1] < ui_sequence["ExecuteAction"][1]) or ui_sequence["ExitDialog"][1] != -1:
            raise RuntimeError("Full-UI setup must enter Welcome before installation and show success on completion")
        events = rows("SELECT `Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering` FROM `ControlEvent`", 6)
        route = ["WelcomeDlg", "LicenseAgreementDlg", "InstallDirDlg", "TernShortcutsDlg", "VerifyReadyDlg"]
        for before, after in zip(route, route[1:]):
            if not any(dialog == before and control == "Next" and event == "NewDialog" and target == after
                       for dialog, control, event, target, _, _ in events):
                raise RuntimeError("Broken installer Next navigation: " + before)
            if not any(dialog == after and control == "Back" and event == "NewDialog" and target == before
                       for dialog, control, event, target, _, _ in events):
                raise RuntimeError("Broken installer Back navigation: " + after)
        if not any(d == "LicenseAgreementDlg" and c == "Next" and e == "NewDialog" and 'LicenseAccepted' in condition
                   for d, c, e, _, condition, _ in events):
            raise RuntimeError("License acceptance must gate the next step")
        license_text = rows("SELECT `Text` FROM `Control` WHERE `Dialog_`='LicenseAgreementDlg' AND `Type`='ScrollableText'", 1)
        if len(license_text) != 1 or any(text not in license_text[0][0] for text in
                                       ["MIT License", "Gehan Fernando", "Permission is hereby granted", 'THE SOFTWARE IS PROVIDED']):
            raise RuntimeError("License page must contain Tern's actual MIT license")
        if properties.get("ARPPRODUCTICON") != "TernIcon.ico" or ["TernIcon.ico"] not in rows("SELECT `Name` FROM `Icon`", 1):
            raise RuntimeError("Installed-programs icon is missing")
        shortcuts = rows("SELECT `Shortcut`, `Directory_`, `Target`, `Icon_` FROM `Shortcut`", 4)
        if {row[0] for row in shortcuts} != {"StartMenuShortcut", "DesktopShortcut"} or any(
                target != "[INSTALLFOLDER]Tern.exe" or icon != "TernIcon.ico" for _, _, target, icon in shortcuts):
            raise RuntimeError("Both optional shortcuts must target Tern and use its icon")
        for feature, prop in [("StartMenuFeature", "STARTMENUSHORTCUT"), ("DesktopFeature", "DESKTOPSHORTCUT")]:
            for event, condition in [("AddLocal", prop + ' = "1"'), ("Remove", "NOT " + prop)]:
                if not any(d == "TernShortcutsDlg" and c == "Next" and e == event and arg == feature and cond == condition
                           for d, c, e, arg, cond, _ in events):
                    raise RuntimeError("Shortcut checkbox does not control its feature")
        if properties.get("WIXUI_INSTALLDIR") != "INSTALLFOLDER":
            raise RuntimeError("Folder picker is not connected to the payload directory")
        launch_events = [row for row in events if row[2] == "DoAction" and row[3] == "LaunchTern"]
        if len(launch_events) != 1 or launch_events[0][:2] != ["ExitDialog", "Finish"] or any(
                token not in launch_events[0][4] for token in ["WIXUI_EXITDIALOGOPTIONALCHECKBOX", "NOT Installed", "NOT REMOVE"]):
            raise RuntimeError("Launch must be optional and limited to successful interactive setup")
        sequence = dict(rows("SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`", 2))
        if "LaunchTern" in sequence:
            raise RuntimeError("Silent install, repair and uninstall must not launch Tern")
        if not int(sequence["InstallInitialize"]) < int(sequence["RemoveExistingProducts"]) < int(sequence["InstallFinalize"]):
            raise RuntimeError("Upgrade removal must be rollback-protected")
        upgrades = rows("SELECT `VersionMax`, `Attributes` FROM `Upgrade`", 2)
        if not any(maximum == expected["version"] and int(attributes) & 512 for maximum, attributes in upgrades):
            raise RuntimeError("The wizard MSI must upgrade the earlier same-version candidate")
        return dict(status="passed", version=properties["ProductVersion"], files=len(files), registryValues=len(registry),
                    perUser=True, defaultAssociationUntouched=True, quotedMarkdownCommand=commands[0],
                    wizard=route + ["ProgressDlg", "ExitDialog"], licenseAcceptanceRequired=True,
                    installedProgramsIcon=properties["ARPPRODUCTICON"], optionalShortcuts=True,
                    optionalLaunchOnFinish=True, rollbackProtectedSameVersionUpgrade=True,
                    typography="Segoe UI", brandedLicenseHeading=True,
                    scope="MSI database validation; does not claim interactive installation or Explorer acceptance")
    finally:
        msi.MsiCloseHandle(handle)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("msi", type=Path)
    parser.add_argument("payload", type=Path)
    args = parser.parse_args()
    print(json.dumps(verify(args.msi, args.payload), indent=2))
