"""Build the per-user Windows MSI from the verified portable payload (WiX 3.14.1)."""
from pathlib import Path
import argparse, hashlib, subprocess, uuid, xml.etree.ElementTree as E
import re
from collections import Counter
from package_release import version, records
import json
from installer_branding import build_branding
NS='http://schemas.microsoft.com/wix/2006/wi'
E.register_namespace('',NS)
def add(parent,tag,**attrs): return E.SubElement(parent,'{'+NS+'}'+tag,{k:str(v) for k,v in attrs.items()})
def ident(text): return 'i'+hashlib.sha256(text.encode()).hexdigest()[:30]
def license_rtf(text):
    """Display the repository license verbatim in Windows Installer's RichEdit control."""
    escaped=[]
    for char in text.replace('\r\n','\n').replace('\r','\n'):
        if char=='\n': escaped.append('\\par\n')
        elif char in '\\{}': escaped.append('\\'+char)
        elif ord(char)<128: escaped.append(char)
        else:
            for offset in range(0,len(char.encode('utf-16-le')),2):
                unit=int.from_bytes(char.encode('utf-16-le')[offset:offset+2],'little')
                escaped.append('\\u'+str(unit if unit<32768 else unit-65536)+'?')
    return '{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Segoe UI;}}\\f0\\fs18\n'+''.join(escaped)+'}'

def setup_options(product, target, install, output):
    repository=Path(__file__).resolve().parents[1]
    icon=repository/'src/Tern.Desktop/Assets/tern.ico'
    add(product,'Icon',Id='TernIcon.ico',SourceFile=str(icon))
    add(product,'Property',Id='ARPPRODUCTICON',Value='TernIcon.ico')
    add(product,'Property',Id='ARPNOMODIFY',Value='1')
    add(product,'SetProperty',Id='ARPINSTALLLOCATION',Value='[INSTALLFOLDER]',After='CostFinalize',Sequence='execute')
    location=add(product,'Property',Id='INSTALLFOLDER',Secure='yes')
    add(location,'RegistrySearch',Id='PreviousInstallFolder',Root='HKCU',Key='Software\\Tern\\Installer',Name='InstallLocation',Type='raw',Win64='yes')
    add(product,'Property',Id='WIXUI_INSTALLDIR',Value='INSTALLFOLDER')
    add(product,'Property',Id='STARTMENUSHORTCUT',Value='1',Secure='yes')
    add(product,'Property',Id='DESKTOPSHORTCUT',Secure='yes')
    add(product,'Property',Id='WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT',Value='Launch Tern')
    add(product,'Property',Id='WIXUI_EXITDIALOGOPTIONALCHECKBOX',Value='1')
    add(product,'Property',Id='WixShellExecTarget',Value='[#'+ident('fileTern.exe')+']')
    add(product,'CustomAction',Id='LaunchTern',BinaryKey='WixCA',DllEntry='WixShellExec',Impersonate='yes',Return='ignore')
    add(product,'UIRef',Id='TernSetupUI')
    license_path=output.with_suffix('.license.rtf')
    license_path.write_text(license_rtf((repository/'LICENSE').read_text(encoding='utf-8')),encoding='ascii')
    add(product,'WixVariable',Id='WixUILicenseRtf',Value=str(license_path))
    branding=output.parent/'installer-branding'
    build_branding(branding)
    add(product,'WixVariable',Id='WixUIBannerBmp',Value=str(branding/'tern-banner.bmp'))
    add(product,'WixVariable',Id='WixUIDialogBmp',Value=str(branding/'tern-dialog.bmp'))
    menu=add(target,'Directory',Id='ProgramMenuFolder')
    menu=add(menu,'Directory',Id='TernStartMenuFolder',Name='Tern')
    desktop=add(target,'Directory',Id='DesktopFolder')
    for id,directory,level,prop in [('StartMenu',menu,1,'STARTMENUSHORTCUT'),('Desktop',desktop,2,'DESKTOPSHORTCUT')]:
        feature=add(product,'Feature',Id=id+'Feature',Title=id+' shortcut',Level=level,AllowAdvertise='no')
        add(feature,'Condition',Level='1').text=prop+' = "1"'
        add(feature,'Condition',Level='2').text='NOT '+prop
        component=add(directory,'Component',Id=id+'ShortcutComponent',Guid=str(uuid.uuid5(uuid.NAMESPACE_URL,'https://tern.local/msi/shortcut/'+id)).upper(),Win64='yes')
        add(component,'RegistryValue',Root='HKCU',Key='Software\\Tern\\Installer',Name=id+'Shortcut',Type='integer',Value='1',KeyPath='yes')
        add(component,'Shortcut',Id=id+'Shortcut',Name='Tern',Target='[INSTALLFOLDER]Tern.exe',WorkingDirectory='INSTALLFOLDER',Advertise='no',Icon='TernIcon.ico',IconIndex='0')
        if id=='StartMenu': add(component,'RemoveFolder',Id='RemoveTernStartMenuFolder',On='uninstall')
        add(feature,'ComponentRef',Id=id+'ShortcutComponent')

def build(payload, output, wix):
    payload=payload.resolve();output=output.resolve()
    if output.exists(): raise RuntimeError('Preserve existing MSI; choose a fresh output')
    if not (payload/'package-manifest.json').is_file(): raise RuntimeError('Verified portable payload required')
    manifest=json.loads((payload/'package-manifest.json').read_text(encoding='utf-8'))
    actual=[record for record in records(payload) if record['path']!='package-manifest.json']
    if manifest.get('version')!=version() or manifest.get('selfContained') is not True or actual!=manifest.get('files'):
        raise RuntimeError('MSI payload changed after portable integrity verification')
    output.parent.mkdir(parents=True,exist_ok=True)
    root=E.Element('{'+NS+'}Wix')
    product=add(root,'Product',Id='*',Name='Tern',Language='1033',Version=version(),Manufacturer='Tern',UpgradeCode='4B121DC9-6895-4F7E-97B9-E3FC0FA62B65')
    add(product,'Package',InstallerVersion='500',Compressed='yes',InstallScope='perUser',Platform='x64',Description='Tern '+version()+' Markdown editor')
    # Replace the earlier 1.0.5 candidate as well as older versions; removal is rollback-protected.
    add(product,'MajorUpgrade',AllowSameVersionUpgrades='yes',Schedule='afterInstallInitialize',DowngradeErrorMessage='A newer version of Tern is already installed.')
    add(product,'MediaTemplate',EmbedCab='yes')
    add(product,'Property',Id='ARPNOREPAIR',Value='1')
    add(product,'Property',Id='ARPHELPLINK',Value='https://github.com/gcfernando/tern-markdown-editor')
    target=add(product,'Directory',Id='TARGETDIR',Name='SourceDir')
    local=add(target,'Directory',Id='LocalAppDataFolder')
    programs=add(local,'Directory',Id='TernPrograms',Name='Programs')
    install=add(programs,'Directory',Id='INSTALLFOLDER',Name='Tern')
    dirs={'':install}; feature=add(product,'Feature',Id='Complete',Title='Tern',Level='1')
    feature.set('Absent','disallow')
    setup_options(product,target,install,output)
    for path in sorted(payload.rglob('*')):
        if path.is_symlink(): raise RuntimeError('Links are not allowed in MSI payload')
        if not path.is_file(): continue
        relative=path.relative_to(payload); parent=''
        for part in relative.parts[:-1]:
            key=parent+'/'+part
            if key not in dirs: dirs[key]=add(dirs[parent],'Directory',Id=ident('dir'+key),Name=part)
            parent=key
        cid=ident(str(relative)); component=add(dirs[parent],'Component',Id=cid,Guid=str(uuid.uuid5(uuid.NAMESPACE_URL,'https://tern.local/msi/'+relative.as_posix())).upper(),Win64='yes')
        file=add(component,'File',Id=ident('file'+str(relative)),Source=str(path),Name=path.name)
        if path.name=='e_sqlite3.dll': file.set('DefaultLanguage','0')  # Native, language-neutral SQLite binary.
        add(component,'RegistryValue',Root='HKCU',Key='Software\\Tern\\InstallerFiles',Name=cid,Type='integer',Value='1',KeyPath='yes')
        add(feature,'ComponentRef',Id=cid)
    cleanup=add(install,'Component',Id='DirectoryCleanup',Guid='CF2541DF-B5C7-4413-B1D4-FB2908BA7164',Win64='yes')
    add(cleanup,'RegistryValue',Root='HKCU',Key='Software\\Tern\\Installer',Name='Directories',Type='integer',Value='1',KeyPath='yes')
    add(cleanup,'RegistryValue',Root='HKCU',Key='Software\\Tern\\Installer',Name='InstallLocation',Type='string',Value='[INSTALLFOLDER]')
    for key,directory in dirs.items(): add(cleanup,'RemoveFolder',Id=ident('remove'+key),Directory=directory.get('Id'),On='uninstall')
    add(cleanup,'RemoveFolder',Id='RemoveTernPrograms',Directory='TernPrograms',On='uninstall')
    add(feature,'ComponentRef',Id='DirectoryCleanup')
    assoc=add(install,'Component',Id='MarkdownRegistration',Guid='3964F01B-5381-431F-A9DA-BFA62A551110',Win64='yes')
    command='"[INSTALLFOLDER]Tern.exe" "%1"'
    entries=[
      ('Software\\Classes\\Applications\\Tern.exe','FriendlyAppName','Tern'),
      ('Software\\Classes\\Applications\\Tern.exe\\SupportedTypes','.md',''),
      ('Software\\Classes\\Applications\\Tern.exe\\shell\\open\\command',None,command),
      ('Software\\Classes\\.md\\OpenWithProgids','Tern.Markdown',''),
      ('Software\\Classes\\Tern.Markdown',None,'Markdown document'),
      ('Software\\Classes\\Tern.Markdown\\DefaultIcon',None,'"[INSTALLFOLDER]Tern.exe",0'),
      ('Software\\Classes\\Tern.Markdown\\shell\\open\\command',None,command),
      ('Software\\Classes\\SystemFileAssociations\\.md\\shell\\Tern.Open',None,'Open with Tern'),
      ('Software\\Classes\\SystemFileAssociations\\.md\\shell\\Tern.Open','Icon','[INSTALLFOLDER]Tern.exe'),
      ('Software\\Classes\\SystemFileAssociations\\.md\\shell\\Tern.Open','MultiSelectModel','Single'),
      ('Software\\Classes\\SystemFileAssociations\\.md\\shell\\Tern.Open\\command',None,command),
      ('Software\\Tern\\Capabilities','ApplicationName','Tern'),
      ('Software\\Tern\\Capabilities','ApplicationDescription','Markdown editor'),
      ('Software\\Tern\\Capabilities\\FileAssociations','.md','Tern.Markdown'),
      ('Software\\RegisteredApplications','Tern','Software\\Tern\\Capabilities')]
    for i,(key,name,value) in enumerate(entries):
        attrs=dict(Root='HKCU',Key=key,Type='string',Value=value)
        if name is not None: attrs['Name']=name
        if i==0: attrs['KeyPath']='yes'
        add(assoc,'RegistryValue',**attrs)
    add(feature,'ComponentRef',Id='MarkdownRegistration')
    source=output.with_suffix('.wxs');E.indent(root);E.ElementTree(root).write(source,encoding='utf-8',xml_declaration=True)
    obj=source.with_suffix('.wixobj')
    extensions=['-ext',str(wix/'WixUIExtension.dll'),'-ext',str(wix/'WixUtilExtension.dll')]
    ui=Path(__file__).resolve().parent/'installer/TernUI.wxs'
    ui_obj=output.with_suffix('.ui.wixobj')
    subprocess.run([str(wix/'candle.exe'),'-nologo','-arch','x64',*extensions,'-out',str(obj),str(source)],check=True)
    subprocess.run([str(wix/'candle.exe'),'-nologo','-arch','x64',*extensions,'-out',str(ui_obj),str(ui)],check=True)
    localization=ui.with_name('TernUI.en-us.wxl')
    result=subprocess.run([str(wix/'light.exe'),'-nologo',*extensions,'-cultures:en-us','-loc',str(localization),'-out',str(output),str(obj),str(ui_obj)],capture_output=True,text=True)
    validation=result.stdout+result.stderr
    log=source.with_suffix('.validation.log');log.write_text(validation,encoding='utf-8')
    if result.returncode:
        print(validation)
        result.check_returncode()
    warnings=Counter(re.findall(r'warning [^:]+: (ICE\d+):',validation))
    if warnings: print('MSI validation notices: '+', '.join(f'{name} ({count})' for name,count in warnings.items())+f'. Full output: {log}')
    return output
if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--payload',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--wix',type=Path,required=True);a=p.parse_args();print(build(a.payload,a.output,a.wix))
