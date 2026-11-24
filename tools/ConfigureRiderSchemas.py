"""Register Tern's local manifest profile in this checkout's Rider project."""
from pathlib import Path
import xml.etree.ElementTree as E


def configure():
    root = Path(__file__).resolve().parents[1]
    settings = root / '.idea' / '.idea.Tern' / '.idea' / 'misc.xml'
    if not settings.parent.is_dir():
        raise RuntimeError('Open Tern.slnx in Rider first to create its local project settings.')
    previous = settings.read_bytes() if settings.exists() else None
    document = E.fromstring(previous) if previous is not None else E.Element('project', version='4')
    resources = document.find("component[@name='ProjectResources']")
    if resources is None:
        resources = E.SubElement(document, 'component', name='ProjectResources')
    for schema in sorted((root / 'tools/schemas/windows-manifest').glob('*.xsd')):
        namespace = E.parse(schema).getroot().get('targetNamespace')
        for old in list(resources):
            if old.tag == 'resource' and old.get('url') == namespace:
                resources.remove(old)
        E.SubElement(resources, 'resource', url=namespace, location=schema.as_posix())
    if previous is not None and settings.read_bytes() != previous:
        raise RuntimeError('Rider settings changed during configuration; retry.')
    if previous is not None:
        backup = root.parent / 'tern-build-output/ide-2e-check/misc.xml.before'
        backup.parent.mkdir(parents=True, exist_ok=True)
        if not backup.exists():
            backup.write_bytes(previous)
    E.indent(document)
    E.ElementTree(document).write(settings, encoding='utf-8', xml_declaration=True)
    print('Registered four local manifest namespaces in ' + str(settings))


if __name__ == '__main__':
    configure()
