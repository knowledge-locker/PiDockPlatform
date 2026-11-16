#!/usr/bin/env python3
"""Sign prepared boot inputs and assemble a factory image. No target disk access."""
import argparse
import hashlib
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
from image_components import digest, fat, run

def sign(image, key, epoch):
    signature = subprocess.run(["openssl","dgst","-sha256","-sign",str(key),str(image)],check=True,capture_output=True).stdout
    if len(signature) != 256:
        raise ValueError("Raspberry Pi boot signing requires an RSA-2048 key")
    return f"{digest(image)}\nts: {epoch}\nrsa2048: {signature.hex()}\n"

def verify(image, signature, public_key):
    lines=signature.splitlines()
    if len(lines)!=3 or lines[0]!=digest(image) or not re.fullmatch(r"ts: [0-9]+",lines[1]) or not re.fullmatch(r"rsa2048: [0-9a-f]{512}",lines[2]):
        raise ValueError("Malformed or mismatched boot signature")
    with tempfile.TemporaryDirectory() as temporary:
        raw=Path(temporary)/"signature"
        raw.write_bytes(bytes.fromhex(lines[2].split(": ",1)[1]))
        run("openssl","dgst","-sha256","-verify",public_key,"-signature",raw,image)

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inputs",required=True,type=Path)
    parser.add_argument("--output",required=True,type=Path)
    parser.add_argument("--key",required=True,type=Path)
    parser.add_argument("--epoch",required=True,type=int)
    parser.add_argument("--genimage",default="genimage")
    args=parser.parse_args()
    if args.key.stat().st_mode & 0o077:
        raise ValueError("Private boot key must be readable only by its owner")
    args.output.mkdir(parents=True,exist_ok=False)
    public=args.output/"boot-public.pem"
    public.write_text(run("openssl","pkey","-in",args.key,"-pubout"))
    description=run("openssl","pkey","-pubin","-in",public,"-text","-noout")
    if "(2048 bit)" not in description:
        raise ValueError("An RSA-2048 boot key is required")
    system=args.output/"system";system.mkdir()
    shutil.copy2(args.inputs/"rootfs.verity",system/"rootfs.verity")
    for slot in ("A","B","recovery"):
        image=args.inputs/f"boot-{slot}.img"
        signature=sign(image,args.key,args.epoch)
        verify(image,signature,public)
        with tempfile.TemporaryDirectory() as temporary:
            source=Path(temporary)
            shutil.copy2(image,source/"boot.img")
            (source/"boot.sig").write_text(signature)
            (source/"config.txt").write_text("[all]\narm_64bit=1\nboot_ramdisk=1\n")
            if slot=="recovery":
                (source/"autoboot.txt").write_text("[all]\ntryboot_a_b=1\nboot_partition=2\n[tryboot]\nboot_partition=3\n")
                # Writable personalization stays outside boot.img. The signed OS accepts it only
                # during initial physical-media setup; it cannot replace an existing owner.
                (source/"pidock-provisioning.json").write_bytes(b"{}"+b" "*((1<<20)-2))
                destination=args.output/"control.vfat"
            else:
                destination=system/f"boot-{slot}.vfat"
            for file in source.iterdir():
                os.utime(file,(args.epoch,args.epoch))
            fat(source,destination,192)
    # Empty, per-device managed state. Device identity and owner credentials are created on first boot.
    data=args.output/"data.ext4"
    with data.open("xb") as output: output.truncate(4096<<20)
    run("mkfs.ext4","-q","-F","-L","pidock-data","-O","metadata_csum,64bit",data)
    board=Path(__file__).resolve().parents[1]/"buildroot/board/pidock"
    with tempfile.TemporaryDirectory() as temporary:
        run(args.genimage,"--rootpath",temporary,"--tmppath",str(Path(temporary)/"work"),"--inputpath",args.output,"--outputpath",args.output,"--config",board/"genimage.cfg")
    print("Factory image assembled. Firmware OTP provisioning and hardware acceptance have not been performed.")

if __name__=="__main__": main()
