#!/usr/bin/env python3
"""Identity-checked SD writer. Defaults to inspection; --write requires root and erases the selected card."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import stat
import subprocess
import sys

def command(*args, timeout=15):
    return subprocess.run(list(args),check=True,capture_output=True,timeout=timeout).stdout

def walk(value):
    if isinstance(value,dict):
        yield value
        for child in value.values(): yield from walk(child)
    elif isinstance(value,list):
        for child in value: yield from walk(child)

def inventory(device):
    media=plistlib.loads(command('/usr/sbin/ioreg','-a','-r','-c','IOMedia'))
    records=[item for item in walk(media) if item.get('BSD Name')==device]
    cards=json.loads(command('/usr/sbin/system_profiler','-json','SPCardReaderDataType'))
    identities=[item for item in walk(cards) if item.get('bsd_name')==device and 'spcardreader_card_serialnumber' in item]
    if len(records)!=1 or len(identities)!=1: raise ValueError('Selected device is not a uniquely identified SD card')
    return records[0],identities[0]

def validate(device,media,identity,serial,size,image_size):
    if not re.fullmatch(r'disk[1-9][0-9]*',device): raise ValueError('Select a whole SD disk, never a partition or the system disk')
    if media.get('BSD Name')!=device or media.get('Whole') is not True or media.get('Removable') is not True or media.get('Writable') is not True:
        raise ValueError('Selected disk is not writable removable whole media')
    if media.get('Size')!=size or identity.get('size_in_bytes')!=size or identity.get('bsd_name')!=device or identity.get('spcardreader_card_serialnumber')!=serial:
        raise ValueError('SD card identity changed; no write permitted')
    if image_size<=0 or image_size>size: raise ValueError('Image does not fit this card')

def checksum(descriptor,length):
    result=hashlib.sha256()
    remaining=length
    while remaining:
        block=os.read(descriptor,min(4<<20,remaining))
        if not block: raise IOError('Unexpected end of image or device')
        result.update(block);remaining-=len(block)
    return result.hexdigest()

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--image',required=True,type=Path)
    parser.add_argument('--sha256',required=True)
    parser.add_argument('--device',required=True)
    parser.add_argument('--card-serial',required=True)
    parser.add_argument('--card-bytes',required=True,type=int)
    parser.add_argument('--write',action='store_true')
    args=parser.parse_args()
    if sys.platform!='darwin': raise ValueError('This writer is for the Mac built-in SD reader')
    if not re.fullmatch(r'[0-9a-f]{64}',args.sha256): raise ValueError('An independently recorded image SHA-256 is required')
    # Keep the same input descriptor across validation and writing; never create a missing device path.
    source=os.open(args.image,os.O_RDONLY|os.O_NOFOLLOW)
    try:
        info=os.fstat(source)
        if not stat.S_ISREG(info.st_mode): raise ValueError('Image must be a regular file')
        media,identity=inventory(args.device)
        validate(args.device,media,identity,args.card_serial,args.card_bytes,info.st_size)
        actual=checksum(source,info.st_size)
        if actual!=args.sha256: raise ValueError('Image checksum mismatch; no write permitted')
        print(json.dumps(dict(action='validated',device=args.device,bytes=info.st_size,sha256=actual)),flush=True)
        if not args.write: return
        if os.geteuid()!=0: raise PermissionError('Writing the SD card requires a private macOS administrator authorization')
        command('/usr/sbin/diskutil','unmountDisk','/dev/'+args.device,timeout=30)
        media,identity=inventory(args.device)
        validate(args.device,media,identity,args.card_serial,args.card_bytes,info.st_size)
        target=os.open('/dev/r'+args.device,os.O_RDWR|os.O_NOFOLLOW)
        try:
            if not stat.S_ISCHR(os.fstat(target).st_mode): raise ValueError('Raw target is not a device')
            media,identity=inventory(args.device)
            validate(args.device,media,identity,args.card_serial,args.card_bytes,info.st_size)
            os.lseek(source,0,os.SEEK_SET)
            remaining=info.st_size
            while remaining:
                block=os.read(source,min(4<<20,remaining))
                if not block: raise IOError('Source image changed during writing')
                view=memoryview(block)
                while view:
                    count=os.write(target,view)
                    if count<=0:raise IOError('SD write did not make progress')
                    view=view[count:]
                remaining-=len(block)
            # Raw-device reads bypass the filesystem cache. Verify exactly the written image extent.
            command('/bin/sync',timeout=30)
            os.lseek(target,0,os.SEEK_SET)
            if checksum(target,info.st_size)!=actual: raise IOError('SD readback verification failed; do not boot this card')
        finally: os.close(target)
        command('/usr/sbin/diskutil','eject','/dev/'+args.device,timeout=30)
        print(json.dumps(dict(action='written-and-readback-verified',device=args.device,bytes=info.st_size,sha256=actual)),flush=True)
    finally: os.close(source)

if __name__=='__main__': main()
