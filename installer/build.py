"""Builds the Sable installer: publishes Sable self-contained for win-x64, then compiles sable.iss with Inno Setup.

    python installer/build.py            # -> installer/output/SableSetup-<version>.exe
    python installer/build.py --no-setup # publish only (installer/publish)

The version comes from <Version> in src/Sable/Sable.csproj. Inno Setup 6 is found in Program Files, on PATH, or at
the ISCC environment variable.
"""
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
CSPROJ = os.path.join(ROOT, "src", "Sable", "Sable.csproj")
PUBLISH = os.path.join(HERE, "publish")
ISS = os.path.join(HERE, "sable.iss")


def version():
    with open(CSPROJ, encoding="utf-8") as f:
        match = re.search(r"<Version>([^<]+)</Version>", f.read())
    if not match:
        sys.exit("No <Version> in Sable.csproj.")
    return match.group(1).strip()


def find_iscc():
    candidates = [
        os.environ.get("ISCC"),
        shutil.which("ISCC"),
        os.path.join(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"), "Inno Setup 6", "ISCC.exe"),
        os.path.join(os.environ.get("ProgramFiles", r"C:\Program Files"), "Inno Setup 6", "ISCC.exe"),
        os.path.join(os.environ.get("LOCALAPPDATA", ""), "Programs", "Inno Setup 6", "ISCC.exe"),
    ]
    for path in candidates:
        if path and os.path.isfile(path):
            return path
    sys.exit("Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php or set ISCC.")


def run(args):
    print(">", " ".join(f'"{a}"' if " " in a else a for a in args), flush=True)
    if subprocess.run(args, cwd=ROOT).returncode != 0:
        sys.exit(f"Failed: {args[0]}")


def main():
    v = version()
    print(f"Sable {v}")

    # A clean publish folder, so files from older builds don't end up in the installer.
    shutil.rmtree(PUBLISH, ignore_errors=True)
    run([
        "dotnet", "publish", CSPROJ,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:DebugType=none",
        "-o", PUBLISH,
    ])
    if not os.path.isfile(os.path.join(PUBLISH, "Sable.exe")):
        sys.exit("Publish produced no Sable.exe.")
    size = sum(os.path.getsize(os.path.join(d, f)) for d, _, files in os.walk(PUBLISH) for f in files)
    print(f"Published to {PUBLISH} ({size / 1e6:.0f} MB)")

    if "--no-setup" in sys.argv:
        return
    run([find_iscc(), f"/DAppVersion={v}", f"/DPublishDir={PUBLISH}", ISS])
    print(f"\nInstaller: {os.path.join(HERE, 'output', f'SableSetup-{v}.exe')}")


if __name__ == "__main__":
    main()
