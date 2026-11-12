"""Strict reader for PiDockOS's MBR/extended partition layout; never modifies a disk."""
from dataclasses import dataclass
import struct

@dataclass(frozen=True)
class Partition:
    number:int
    start:int
    sectors:int
    kind:int

def partitions(read, total_bytes):
    def sector(number):
        if number<0 or (number+1)*512>total_bytes:raise ValueError('Partition table outside media')
        value=read(number*512,512)
        if len(value)!=512 or value[510:]!=b'\x55\xaa':raise ValueError('Invalid MBR/EBR signature')
        return value
    def entries(value):
        return [(value[446+n*16+4],*struct.unpack_from('<II',value,446+n*16+8)) for n in range(4)]
    mbr=sector(0)
    if struct.unpack_from('<I',mbr,440)[0]!=0x5049444b:raise ValueError('This is not a PiDockOS disk')
    result=[];extended=None
    for number,(kind,start,size) in enumerate(entries(mbr),1):
        if not kind or not size:raise ValueError('PiDockOS requires all four primary table entries')
        if start<=0 or (start+size)*512>total_bytes:raise ValueError('Primary partition outside media')
        result.append(Partition(number,start,size,kind))
        if kind in (5,15):
            if number!=4 or extended is not None:raise ValueError('Unsupported extended partition')
            extended=(start,size)
    if extended is None:raise ValueError('Extended partition missing')
    base,length=extended;current=base;seen=set()
    while True:
        if current in seen or len(seen)>8:raise ValueError('Cyclic or oversized extended table')
        seen.add(current);items=entries(sector(current));kind,relative,size=items[0]
        start=current+relative
        if kind not in (0x83,) or relative<=0 or size<=0 or start<base or start+size>base+length:raise ValueError('Invalid logical partition')
        result.append(Partition(4+len(seen),start,size,kind))
        link,offset,count=items[1]
        if not link and not offset and not count:break
        if link not in (5,15) or offset<=0 or count<=0:raise ValueError('Invalid extended link')
        current=base+offset
        if not base<=current<base+length:raise ValueError('Extended link outside its container')
    if [p.number for p in result]!=list(range(1,8)) or any(p.kind!=0x0c for p in result[:3]):raise ValueError('Unsupported PiDockOS partition layout')
    ordinary=[p for p in result if p.number!=4]
    for before,after in zip(ordinary,ordinary[1:]):
        if before.start+before.sectors>after.start:raise ValueError('Overlapping partitions')
    return {p.number:p for p in result}
