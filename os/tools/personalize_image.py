#!/usr/bin/env python3
"""Add private data to a COPY of the public factory image, without changing signed system bytes."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys
from disk_layout import partitions,grow_data_partition
from image_components import digest

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--factory',required=True,type=Path)
    parser.add_argument('--data',required=True,type=Path)
    parser.add_argument('--output',required=True,type=Path)
    parser.add_argument('--card-bytes',type=int)
    args=parser.parse_args()
    if args.output.exists() or args.output.is_symlink():raise ValueError('Refusing to overwrite an existing image')
    with args.factory.open('rb') as source:
        def read(offset,size):source.seek(offset);return source.read(size)
        data=partitions(read,args.factory.stat().st_size)[7]
    if args.data.stat().st_size!=data.sectors*512:raise ValueError('Private filesystem must match the factory data partition size')
    if sys.platform=='darwin':subprocess.run(['/bin/cp','-c',str(args.factory.resolve()),str(args.output.resolve())],check=True)
    else:shutil.copyfile(args.factory,args.output)
    args.output.chmod(0o600)
    with args.output.open('r+b') as target,args.data.open('rb') as source:
        target.seek(data.start*512);shutil.copyfileobj(source,target,4<<20);target.flush();os.fsync(target.fileno())
    if args.card_bytes is not None:grow_data_partition(args.output,args.card_bytes)
    print('Private personalized image prepared. Do not publish or share it.')
    print('SHA-256: '+digest(args.output))

if __name__=='__main__':main()
