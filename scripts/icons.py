import struct,zlib
from pathlib import Path
p=Path('src/Sem.Web/wwwroot')
def chunk(t,d):return struct.pack('>I',len(d))+t+d+struct.pack('>I',zlib.crc32(t+d)&0xffffffff)
for size in (192,512):
 raw=b''.join(b'\0'+bytes([22,101,52])*size for _ in range(size))
 data=b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',size,size,8,2,0,0,0))+chunk(b'IDAT',zlib.compress(raw))+chunk(b'IEND',b'')
 (p/('sem-icon-%d.png'%size)).write_bytes(data)
