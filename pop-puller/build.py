"""Build the standalone, receipt-only GFW8 Pop Puller for Windows x64."""
from pathlib import Path
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile

HERE = Path(__file__).resolve().parent
MAIN = HERE.parent
if not (MAIN / 'backend/games.json').is_file():
    MAIN = HERE / 'main-snapshot'
if not (MAIN / 'backend/games.json').is_file():
    raise RuntimeError('Build needs the adjacent Grabber source or main-snapshot.')
VERSION = re.search(r'Version = "([0-9.]+)"', (HERE / 'Runtime.cs').read_text(encoding='utf-8')).group(1)
OUT = (HERE / 'dist') if MAIN.name == 'main-snapshot' else MAIN / 'dist' / ('pop-puller-v' + VERSION)
STAGE = HERE / 'build' / 'runtime'

def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def main():
    if os.name != 'nt':
        raise RuntimeError('Build on Windows with .NET Framework 4.8.')
    OUT.mkdir(parents=True, exist_ok=True)
    STAGE.mkdir(parents=True, exist_ok=True)
    files = {
        'SharingBackend.ps1': MAIN / 'src/sharing/SharingBackend.ps1',
        'GuiBackend.ps1': MAIN / 'src/ui/GuiBackend.ps1',
        'ReceiptStorage.ps1': MAIN / 'src/ui/ReceiptStorage.ps1',
        'InstalledGames.ps1': MAIN / 'src/ui/InstalledGames.ps1',
        'backend/Core.ps1': MAIN / 'backend/Core.ps1',
        'backend/games.json': MAIN / 'backend/games.json',
    }
    for name in ('FileHash.ps1', 'PublicWam.ps1', 'receipt-public.cer'):
        files['backend/runtime/standalone-receipt-tool/' + name] = MAIN / 'backend/runtime/standalone-receipt-tool' / name
    for name, source in files.items():
        destination = STAGE / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)
    payload = HERE / 'build/payload.zip'
    with zipfile.ZipFile(payload, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name in sorted(files):
            archive.writestr(name, (STAGE / name).read_bytes())
    sys.path.insert(0, str(MAIN / 'tools'))
    import exe_metadata
    assembly = exe_metadata.assembly_info(HERE / 'build/AssemblyInfo.cs', 'GFW8 Pop Puller',
                                          'Private group Windows 8 receipt utility', VERSION)
    with open(assembly, 'a', encoding='utf-8') as output:
        output.write('[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName=".NET Framework 4.8")]\n')
    compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    exe = OUT / ('GFW8 Pop Puller v' + VERSION + '.exe')
    subprocess.run([str(compiler), '/nologo', '/target:winexe', '/platform:x64', '/optimize+',
                    '/win32icon:' + str(HERE / 'PopPuller.ico'),
                    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
                    '/reference:System.Web.Extensions.dll', '/reference:System.Security.dll',
                    '/reference:System.Net.Http.dll', '/reference:System.IO.Compression.dll',
                    '/resource:' + str(payload) + ',PopPuller.Payload',
                    '/resource:' + str(HERE / 'PopPuller.ico') + ',PopPuller.Icon', '/out:' + str(exe),
                    str(HERE / 'PopPuller.cs'), str(HERE / 'HubClient.cs'), str(HERE / 'Runtime.cs'),
                    str(MAIN / 'src/sharing/ShareCrypto.cs'), str(assembly)], check=True)
    verified = HERE / 'build/verified.json'
    subprocess.run([str(exe), '--verify-bundle', str(verified)], check=True)
    assert json.loads(verified.read_text(encoding='utf-8'))['verified']
    subprocess.run([str(exe), '--self-test'], check=True)
    details = exe_metadata.verify(exe, 'GFW8 Pop Puller', VERSION)
    (OUT / 'FILE-DETAILS.json').write_text(json.dumps([details], indent=2) + '\n', encoding='utf-8')
    (OUT / 'SHA256SUMS.txt').write_text(sha(exe) + '  ' + exe.name + '\n', encoding='ascii')
    source = OUT / ('GFW8-Pop-Puller-Source-v' + VERSION + '.zip')
    with zipfile.ZipFile(source, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name in ('PopPuller.cs', 'HubClient.cs', 'Runtime.cs', 'build.py', 'make_icon.py', 'PopPuller.ico', 'Issue-Code.ps1'):
            archive.write(HERE / name, 'pop-puller/' + name)
        for name in ('src/sharing/ShareCrypto.cs', 'src/ui/App.ico'):
            archive.write(MAIN / name, 'main-snapshot/' + name)
        for name, item in files.items():
            archive.write(item, 'main-snapshot/' + ('src/sharing/' if name == 'SharingBackend.ps1' else 'src/ui/' if name in ('GuiBackend.ps1', 'ReceiptStorage.ps1', 'InstalledGames.ps1') else '') + name)
        for name in ('wrangler.jsonc', 'package.json', 'package-lock.json',
                     'src/index.js', 'src/leaderboard.js', 'test-hub.mjs', 'live-smoke.mjs'):
            archive.write(HERE / 'hub' / name, 'pop-puller/hub/' + name)
        archive.write(MAIN / 'tools/exe_metadata.py', 'main-snapshot/tools/exe_metadata.py')
    info = {'version': VERSION, 'exe': exe.name, 'exeSha256': sha(exe), 'payloadSha256': sha(payload),
            'sourceSha256': sha(source), 'runtimeFiles': {name: sha(STAGE / name) for name in files}}
    (OUT / 'BUILD-INFO.json').write_text(json.dumps(info, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'exe': str(exe), 'sha256': sha(exe), 'source': str(source)}))

if __name__ == '__main__':
    main()
