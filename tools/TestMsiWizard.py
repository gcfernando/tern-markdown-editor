"""Exercise MSI costing/selection with a restricted handle incapable of installation.

MSIOPENPACKAGEFLAGS_IGNOREMACHINESTATE prevents changes to the computer and
prevents executable/DLL/script custom actions. Never run INSTALL or a UI dialog.
"""
import argparse
import ctypes as C
from ctypes import wintypes as W
import json
from pathlib import Path


def verify(path):
    ole = C.WinDLL('ole32')
    result = ole.CoInitializeEx(None, 2)
    if result not in (0, 1):
        raise RuntimeError('Could not initialize the MSI test apartment')
    msi = C.WinDLL('msi')
    msi.MsiOpenPackageExW.argtypes = [W.LPCWSTR, W.DWORD, C.POINTER(W.UINT)]
    msi.MsiSetPropertyW.argtypes = [W.UINT, W.LPCWSTR, W.LPCWSTR]
    msi.MsiDoActionW.argtypes = [W.UINT, W.LPCWSTR]
    msi.MsiSetInstallLevel.argtypes = [W.UINT, C.c_int]
    msi.MsiSetFeatureStateW.argtypes = [W.UINT, W.LPCWSTR, C.c_int]
    msi.MsiGetFeatureStateW.argtypes = [W.UINT, W.LPCWSTR, C.POINTER(C.c_int), C.POINTER(C.c_int)]
    msi.MsiGetComponentStateW.argtypes = msi.MsiGetFeatureStateW.argtypes
    msi.MsiSetTargetPathW.argtypes = [W.UINT, W.LPCWSTR, W.LPCWSTR]
    msi.MsiGetTargetPathW.argtypes = [W.UINT, W.LPCWSTR, W.LPWSTR, C.POINTER(W.DWORD)]
    msi.MsiEvaluateConditionW.argtypes = [W.UINT, W.LPCWSTR]
    handle = W.UINT()

    def check(code):
        if code:
            raise RuntimeError(f'Restricted MSI selection check failed: {code}')

    def state(name, component=False):
        installed = C.c_int(); requested = C.c_int()
        query = msi.MsiGetComponentStateW if component else msi.MsiGetFeatureStateW
        check(query(handle, name, C.byref(installed), C.byref(requested)))
        return requested.value

    try:
        check(msi.MsiOpenPackageExW(str(path.resolve()), 1, C.byref(handle)))
        # Costing reads metadata and computes a plan; this restricted handle cannot install.
        for action in ('CostInitialize', 'FileCost', 'CostFinalize'):
            check(msi.MsiDoActionW(handle, action))
        check(msi.MsiSetInstallLevel(handle, 1))
        defaults = {name: state(name) for name in ('Complete', 'StartMenuFeature', 'DesktopFeature')}
        # An excluded, never-installed feature has no action (-1); an explicit
        # removal is Absent (2). Neither may request the desktop shortcut.
        if defaults['Complete'] != 3 or defaults['StartMenuFeature'] != 3 or defaults['DesktopFeature'] not in (-1, 2):
            raise RuntimeError('Default plan must install core + Start menu, without a desktop shortcut: ' + str(defaults))
        combinations = []
        for start, desktop in ((False, False), (True, False), (False, True), (True, True)):
            for name, selected in (('StartMenu', start), ('Desktop', desktop)):
                check(msi.MsiSetFeatureStateW(handle, name + 'Feature', 3 if selected else 2))
                feature_state = state(name + 'Feature')
                component_state = state(name + 'ShortcutComponent', True)
                if feature_state != (3 if selected else 2) or component_state not in ((3,) if selected else (-1, 2)):
                    raise RuntimeError(f'Shortcut choice did not update its installation plan: {name}, selected={selected}, feature={feature_state}, component={component_state}')
            if state('Complete') != 3:
                raise RuntimeError('Shortcut choices changed the required application feature')
            combinations.append(dict(startMenu=start, desktop=desktop, passed=True))
        if msi.MsiEvaluateConditionW(handle, 'LicenseAccepted = "1"') != 0:
            raise RuntimeError('License must not be preaccepted')
        check(msi.MsiSetPropertyW(handle, 'LicenseAccepted', '1'))
        if msi.MsiEvaluateConditionW(handle, 'LicenseAccepted = "1"') != 1:
            raise RuntimeError('License acceptance condition failed')
        target = str(path.resolve().parent / 'chosen folder with spaces' / 'Tern') + '\\'
        existed = Path(target).exists()
        check(msi.MsiSetTargetPathW(handle, 'INSTALLFOLDER', target))
        size = W.DWORD(32768); actual = C.create_unicode_buffer(size.value)
        check(msi.MsiGetTargetPathW(handle, 'INSTALLFOLDER', actual, C.byref(size)))
        if actual.value.casefold() != target.casefold() or Path(target).exists() != existed:
            raise RuntimeError('Chosen installation path was not preserved without writing files')
        return dict(status='passed', restrictedHandle=True, defaults='Core and Start menu; no desktop shortcut',
                    shortcutCombinations=combinations, licenseAcceptance=True, chosenPathWithSpaces=True,
                    scope='Windows Installer costing and selection only. No install, registry/default change, GUI interaction or launch.')
    finally:
        if handle.value:
            msi.MsiCloseHandle(handle)
        ole.CoUninitialize()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('msi', type=Path)
    print(json.dumps(verify(parser.parse_args().msi), indent=2))
