#!/usr/bin/env python3
"""Build verity and signed-boot inputs. Never flashes a disk or programs OTP."""
import argparse
import gzip
import hashlib
import io
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import tempfile

ROOT_LIMIT = 1664 << 20
DISK_SIGNATURE = "5049444b"
SLOTS = {"A": 5, "B": 6}

def run(*args, **kwargs):
    try:
        return subprocess.run([str(x) for x in args], check=True, text=True, capture_output=True, **kwargs).stdout
    except subprocess.CalledProcessError as error:
        if hasattr(error,"add_note"):
            error.add_note((error.stderr or "")[:4096])
        raise

def digest(path):
    with Path(path).open("rb") as source:
        checksum=hashlib.sha256()
        for chunk in iter(lambda:source.read(1<<20),b""):
            checksum.update(chunk)
        return checksum.hexdigest()

def resolve_target(target, name):
    """Resolve absolute target symlinks inside the sysroot, never on the build host."""
    pending = list(PurePosixPath(name).parts)
    current = []
    for _ in range(128):
        if not pending:
            return target.joinpath(*current)
        part = pending.pop(0)
        if part in ("/", ".", ""):
            continue
        if part == "..":
            if not current:
                raise ValueError("Target symlink escapes sysroot")
            current.pop()
            continue
        candidate = target.joinpath(*current, part)
        if candidate.is_symlink():
            link = PurePosixPath(os.readlink(candidate))
            if link.is_absolute():
                current = []
            pending = list(link.parts) + pending
        else:
            current.append(part)
    raise ValueError("Target symlink loop")

def copy_binary(target, dest, name, seen=None):
    seen = set() if seen is None else seen
    if name in seen:
        return
    seen.add(name)
    source = resolve_target(target, name)
    canonical="/"+source.relative_to(target).as_posix()
    if name != canonical:
        copy_binary(target,dest,canonical,seen)
        alias=dest/name.lstrip("/")
        alias.parent.mkdir(parents=True,exist_ok=True)
        if alias.is_symlink() or alias.is_file():
            alias.unlink()
        alias.symlink_to(canonical)
        return
    output = dest / name.lstrip("/")
    output.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, output)
    headers = run("readelf", "-l", source)
    interpreter = re.search(r"Requesting program interpreter: ([^\]]+)", headers)
    if interpreter:
        copy_binary(target, dest, interpreter[1], seen)
    dynamic = run("readelf", "-d", source)
    for library in re.findall(r"Shared library: \[([^\]]+)\]", dynamic):
        for folder in (str(PurePosixPath(name).parent), "/lib", "/usr/lib", "/lib64", "/usr/lib64"):
            candidate = folder + "/" + library
            if resolve_target(target, candidate).is_file():
                copy_binary(target, dest, candidate, seen)
                break
        else:
            raise ValueError("Missing target dependency: " + library)

def newc(root, epoch):
    """Bounded deterministic newc archive; all files owned by root, no device nodes."""
    output = io.BytesIO()
    def entry(name, mode, payload, inode):
        encoded = name.encode() + b"\0"
        fields = (inode, mode, 0, 0, 1, epoch, len(payload), 0, 0, 0, 0, len(encoded), 0)
        output.write(b"070701" + b"".join(f"{x:08x}".encode() for x in fields))
        output.write(encoded)
        output.write(b"\0" * (-output.tell() % 4))
        output.write(payload)
        output.write(b"\0" * (-output.tell() % 4))
    for inode, path in enumerate(sorted(root.rglob("*")), 1):
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode):
            payload = os.readlink(path).encode()
        elif stat.S_ISREG(info.st_mode):
            payload = path.read_bytes()
        elif stat.S_ISDIR(info.st_mode):
            payload = b""
        else:
            raise ValueError("Special file in initramfs")
        entry(path.relative_to(root).as_posix(), info.st_mode, payload, inode)
    entry("TRAILER!!!", 0, b"", 0)
    return output.getvalue()

def make_initramfs(target, init, output, root_hash, hash_offset, partition, mode, epoch):
    if not re.fullmatch(r"[0-9a-f]{64}", root_hash) or partition not in (5, 6) or mode not in ("normal", "recovery"):
        raise ValueError("Invalid signed root descriptor")
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        for folder in ("bin", "sbin", "dev", "proc", "sys", "run", "newroot", "etc"):
            (root / folder).mkdir()
        copy_binary(target, root, "/bin/busybox")
        copy_binary(target, root, "/usr/lib/pidock/boot-reboot")
        (root / "bin/boot-reboot").symlink_to("/usr/lib/pidock/boot-reboot")
        for name in ("sh", "mount", "switch_root", "sleep", "reboot", "seq", "grep", "mkdir", "chmod", "date", "cat"):
            (root / "bin" / name).symlink_to("busybox")
        for program in ("veritysetup", "blkid"):
            found = next((folder + "/" + program for folder in ("/sbin", "/usr/sbin", "/bin", "/usr/bin") if resolve_target(target, folder + "/" + program).is_file()), None)
            if not found:
                raise ValueError("Missing target tool: " + program)
            copy_binary(target, root, found)
            if found != "/bin/" + program:
                (root / "bin" / program).symlink_to(found)
        shutil.copy2(init, root / "init")
        (root / "init").chmod(0o755)
        (root / "etc/root.env").write_text(f"ROOT_PARTUUID={DISK_SIGNATURE}-{partition:02d}\nROOT_HASH={root_hash}\nHASH_OFFSET={hash_offset}\nBOOT_MODE={mode}\n")
        (root / "etc/build-epoch").write_text(str(epoch)+"\n")
        with output.open("xb") as destination:
            with gzip.GzipFile(fileobj=destination, mode="wb", mtime=epoch, filename="") as compressed:
                compressed.write(newc(root, epoch))

def make_recovery_initramfs(target, scripts, output, epoch):
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        for folder in ("bin", "sbin", "dev", "proc", "sys", "run", "etc", "tmp", "state"):
            (root / folder).mkdir()
        (root / "tmp").chmod(0o1777)
        copy_binary(target, root, "/bin/busybox")
        copy_binary(target, root, "/usr/lib/pidock/boot-reboot")
        (root / "bin/boot-reboot").symlink_to("/usr/lib/pidock/boot-reboot")
        for name in ("sh", "mount", "umount", "mountpoint", "sleep", "reboot", "grep", "mkdir", "chmod", "chown", "date", "sync", "cat"):
            (root / "bin" / name).symlink_to("busybox")
        for program in ("blkid", "setpriv"):
            found = next((folder + "/" + program for folder in ("/usr/sbin", "/usr/bin", "/sbin", "/bin") if resolve_target(target, folder + "/" + program).is_file()), None)
            if not found:
                raise ValueError("Missing recovery tool: " + program)
            copy_binary(target, root, found)
            if found != "/bin/" + program:
                (root / "bin" / program).symlink_to(found)
        payload = target / "usr/lib/pidock/appliance"
        shutil.copytree(payload, root / "usr/lib/pidock/appliance", symlinks=True)
        # Share the appliance's serviced self-contained runtime with the offline image repair command.
        for name in ("PiDock.Updater", "PiDock.Updater.dll", "PiDock.Updater.deps.json", "PiDock.Updater.runtimeconfig.json"):
            shutil.copy2(target / "usr/lib/pidock/updater" / name, root / "usr/lib/pidock/appliance" / name)
        shutil.copytree(target / "etc/pidock/update-trust", root / "etc/pidock/update-trust")
        shutil.copy2(target / "etc/pidock/image-profile.json", root / "etc/pidock/recovery-profile.json")
        for binary in [payload / "PiDock.Appliance.Web", *payload.glob("*.so")]:
            if binary.name == "libcoreclrtraceptprovider.so":
                # The optional LTTng provider requires an older external ABI. Offline recovery
                # uses normal console/EventPipe diagnostics and does not ship an unused provider.
                (root / "usr/lib/pidock/appliance" / binary.name).unlink()
                continue
            copy_binary(target, root, "/" + binary.relative_to(target).as_posix())
        for folder in ("lib", "usr/lib"):
            for pattern in ("libicu*.so*", "libssl.so*", "libcrypto.so*"):
                for library in (target / folder).glob(pattern):
                    copy_binary(target, root, "/" + library.relative_to(target).as_posix())
        shutil.copy2(scripts / "recovery-init", root / "init")
        shutil.copy2(scripts / "recovery-menu", root / "bin/pidock-recovery")
        for path in (root / "init", root / "bin/pidock-recovery"):
            path.chmod(0o755)
        (root / "etc/build-epoch").write_text(str(epoch)+"\n")
        for file in ("passwd", "group", "nsswitch.conf"):
            source=resolve_target(target,"/etc/"+file)
            if source.is_file():
                shutil.copy2(source,root/"etc"/file)
        with output.open("xb") as destination:
            with gzip.GzipFile(fileobj=destination, mode="wb", mtime=epoch, filename="") as compressed:
                compressed.write(newc(root, epoch))

def verity(rootfs, output, epoch):
    length = rootfs.stat().st_size
    if length <= 0 or length % 4096 or length >= ROOT_LIMIT:
        raise ValueError("Root filesystem must be 4096-byte aligned and fit its slot")
    with tempfile.TemporaryDirectory() as temporary:
        tree = Path(temporary) / "hashes"
        # Salt is public; derive it from the exact filesystem to avoid hidden random build inputs.
        salt = hashlib.sha256((digest(rootfs) + ":pidock-verity-v1").encode()).hexdigest()
        report = run("veritysetup", "format", rootfs, tree, "--hash=sha256", "--salt=" + salt)
        match = re.search(r"Root hash:\s*([0-9a-f]{64})", report)
        if not match or length + tree.stat().st_size > ROOT_LIMIT:
            raise ValueError("Verity tree does not fit the system slot")
        with output.open("xb") as destination, rootfs.open("rb") as source, tree.open("rb") as hashes:
            shutil.copyfileobj(source, destination)
            shutil.copyfileobj(hashes, destination)
        run("veritysetup", "verify", output, output, match[1], "--hash-offset=" + str(length))
        return match[1], length

def fat(source, output, mib=128):
    if output.exists():
        raise ValueError("Refusing to overwrite image output")
    with output.open("xb") as image:
        image.truncate(mib << 20)
    run("mkfs.vfat", "--invariant", "-F", "32", output)
    for path in sorted(source.iterdir()):
        run("mcopy", "-m", "-s", "-i", output, path, "::/")

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", required=True, type=Path)
    parser.add_argument("--images", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--epoch", required=True, type=int)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    root_hash, offset = verity(args.images / "rootfs.ext4", args.output / "rootfs.verity", args.epoch)
    init = Path(__file__).resolve().parents[1] / "buildroot/board/pidock/initramfs/init"
    for slot, partition in {"A": 5, "B": 6, "recovery": 5}.items():
        with tempfile.TemporaryDirectory() as temporary:
            boot = Path(temporary)
            shutil.copy2(args.images / "Image", boot / "Image")
            for dtb in sorted(args.images.glob("bcm2712*.dtb")):
                shutil.copy2(dtb, boot / dtb.name)
            if not list(boot.glob("*.dtb")):
                raise ValueError("Pi 5 device trees missing")
            overlays=args.images / "rpi-firmware/overlays"
            if not overlays.is_dir():
                raise ValueError("Signed display/board overlays are missing")
            shutil.copytree(overlays,boot/"overlays")
            board = init.parent.parent
            shutil.copy2(board / "config.txt", boot / "config.txt")
            shutil.copy2(board / "cmdline.txt", boot / "cmdline.txt")
            if slot == "recovery":
                make_recovery_initramfs(args.target, init.parent, boot / "initramfs.gz", args.epoch)
            else:
                make_initramfs(args.target, init, boot / "initramfs.gz", root_hash, offset, partition, "normal", args.epoch)
            required = sum(p.stat().st_size for p in boot.rglob("*") if p.is_file())
            if required > 120 << 20:
                raise ValueError(f"Boot ramdisk {slot} needs {required >> 20} MiB and exceeds the reviewed 128 MiB profile; reduce recovery payload before release")
            fat(boot, args.output / f"boot-{slot}.img")
    (args.output / "root-descriptor.txt").write_text(f"sha256={root_hash}\nhash-offset={offset}\n")
    print("Verified-root and boot inputs prepared; signing and disk assembly are separate steps.")

if __name__ == "__main__":
    main()
