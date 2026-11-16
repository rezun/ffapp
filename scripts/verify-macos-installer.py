#!/usr/bin/env python3
"""Verify that a single installation provides both discoverable application entries."""
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path
import xml.etree.ElementTree as E

with tempfile.TemporaryDirectory() as directory:
    expanded = Path(directory) / 'expanded'
    subprocess.run(['pkgutil', '--expand', sys.argv[1], str(expanded)], check=True)
    distribution = E.parse(expanded / 'Distribution').getroot()
    refs = [r for r in distribution.findall('pkg-ref') if r.text and r.text.strip().startswith('#')]
    assert {r.attrib['id'] for r in refs} == {'com.ffapp.desktop', 'com.ffapp.ffrux.launcher'}
    for ref in refs:
        component = expanded / ref.text.strip().removeprefix('#')
        info = E.parse(component / 'PackageInfo').getroot()
        assert info.attrib['install-location'] == '/Applications', info.attrib
        bom = subprocess.check_output(['lsbom', '-s', str(component / 'Bom')], text=True)
        if ref.attrib['id'] == 'com.ffapp.ffrux.launcher':
            assert './FFRux.app/Contents/MacOS/FFRuxLauncher' in bom
            assert not info.findall('relocate/bundle'), 'The launcher must not relocate into FFApp.app'
            assert not info.findall('strict-identifier/bundle'), 'Legacy standalone FFRux must be replaceable'
        else:
            assert './FFApp.app/Contents/Helpers/FFRux.app/Contents/MacOS/FFRux' in bom
    assert distribution.find('domains').attrib['enable_currentUserHome'] == 'true'
with zipfile.ZipFile(sys.argv[2]) as archive:
    names = set(archive.namelist())
    assert 'FFRux.app/Contents/MacOS/FFRuxLauncher' in names
    assert 'FFApp.app/Contents/Helpers/FFRux.app/Contents/MacOS/FFRux' in names
print('Verified both macOS application entries in the installer and portable archive.')
