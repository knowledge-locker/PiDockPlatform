#!/usr/bin/env python3
"""Locate the bounded, unsigned setup file in an assembled PiDockOS factory image."""
import argparse,json,struct
from disk_layout import partitions

def extents(read,total):
    partition=partitions(read,total)[1];base=partition.start*512;limit=partition.sectors*512
    def local(offset,count):
        if offset<0 or count<0 or offset+count>limit:raise ValueError('FAT reference outside control partition')
        value=read(base+offset,count)
        if len(value)!=count:raise ValueError('Truncated FAT data')
        return value
    boot=local(0,512);bps=struct.unpack_from('<H',boot,11)[0];spc=boot[13]
    reserved=struct.unpack_from('<H',boot,14)[0];fats=boot[16];roots=struct.unpack_from('<H',boot,17)[0]
    fatsectors=struct.unpack_from('<H',boot,22)[0] or struct.unpack_from('<I',boot,36)[0]
    if bps!=512 or spc not in (1,2,4,8,16,32,64,128) or fats not in (1,2) or not reserved or not fatsectors:raise ValueError('Unsupported FAT layout')
    rootsectors=(roots*32+511)//512;data=(reserved+fats*fatsectors+rootsectors)*512;clusterbytes=spc*512
    count=(limit-data)//clusterbytes;fat32=count>=65525
    if count<4085:raise ValueError('FAT12 unsupported')
    def chain(start):
        seen=set();cluster=start
        while True:
            if cluster<2 or cluster>=count+2 or cluster in seen or len(seen)>32768:raise ValueError('Invalid FAT cluster chain')
            seen.add(cluster);yield data+(cluster-2)*clusterbytes
            size=4 if fat32 else 2
            cluster=int.from_bytes(local(reserved*512+cluster*size,size),'little')& (0x0fffffff if fat32 else 0xffff)
            if cluster>=(0x0ffffff8 if fat32 else 0xfff8):break
    directory=b''.join(local(offset,clusterbytes) for offset in chain(struct.unpack_from('<I',boot,44)[0])) if fat32 else local((reserved+fats*fatsectors)*512,rootsectors*512)
    found=None;longparts={}
    for offset in range(0,len(directory),32):
        item=directory[offset:offset+32]
        if item[0]==0:break
        if item[0]==0xe5:longparts={};continue
        if item[11]==15:
            part=item[0]&31
            if item[0]&64:longparts={}
            longparts[part]=item[1:11]+item[14:26]+item[28:32];continue
        name=b''.join(longparts[k] for k in sorted(longparts)).decode('utf-16le').split('\0')[0] if longparts else ''
        longparts={}
        if name!='pidock-provisioning.json':continue
        if found is not None or item[11]&0x18:raise ValueError('Ambiguous provisioning file')
        size=struct.unpack_from('<I',item,28)[0];start=struct.unpack_from('<H',item,26)[0]+(struct.unpack_from('<H',item,20)[0]<<16 if fat32 else 0)
        if size!=1<<20:raise ValueError('Provisioning file must reserve exactly 1 MiB')
        found=[];remaining=size
        for block in chain(start):
            if remaining<=0:raise ValueError('Excess provisioning clusters')
            length=min(remaining,clusterbytes);absolute=base+block
            if found and absolute==found[-1]['offset']+found[-1]['length']:found[-1]['length']+=length
            else:found.append(dict(offset=absolute,length=length))
            remaining-=length
        if remaining:raise ValueError('Truncated provisioning chain')
    if not found or len(found)>32 or found!=sorted(found,key=lambda x:x['offset']):raise ValueError('Unsupported provisioning extents')
    contents=b''.join(read(p['offset'],p['length']) for p in found)
    if contents.strip()!=b'{}':raise ValueError('Factory image already contains private setup')
    return found

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('image');parser.add_argument('output');args=parser.parse_args()
    with open(args.image,'rb') as source:
        source.seek(0,2);size=source.tell()
        def read(offset,length):source.seek(offset);return source.read(length)
        result=extents(read,size)
    with open(args.output,'x') as output:json.dump(result,output,separators=(',',':'))
