"""Independent HashVer1.3 stream decoder; original archives are read only."""
import argparse, json, pathlib, struct, hashlib

def transform(data, length, decrypt=True):
    b=bytearray(data); k=[0xA73C5F9D]*2
    prev=[((length+0x428)^0xFEC9753E)&0xffffffff]*2
    for i in range(0,length//8*8,8):
        v=struct.unpack_from('<II',b,i)
        k=[((x+0xCE24F523)&0xffffffff)^y for x,y in zip(k,prev)]
        out=[x^y for x,y in zip(v,k)]
        # Native encryption feeds back its plaintext input; decryption feeds back recovered plaintext.
        prev=out if decrypt else v
        struct.pack_into('<II',b,i,*out)
    return bytes(b)

def decompress(b):
    assert b[:4]==b'1PC\xff'
    short=b[4]&1; size=struct.unpack_from('<I',b,8)[0]; p=12; out=bytearray()
    while p<len(b):
        a=list(range(256)); c=[0]*256; i=0
        while i<256:
            count=b[p];p+=1
            if count>127:i+=count-127;count=0
            if i>255:break
            for _ in range(count+1):
                a[i]=b[p];p+=1
                if a[i]!=i:c[i]=b[p];p+=1
                i+=1
        count=struct.unpack_from('<H' if short else '<I',b,p)[0];p+=2 if short else 4
        end=p+count
        while p<end:
            stack=[b[p]];p+=1
            while stack:
                x=stack.pop()
                if a[x]==x:out.append(x)
                else:stack.extend((c[x],a[x]))
    assert len(out)==size,(len(out),size)
    return bytes(out)

def main():
    ap=argparse.ArgumentParser();ap.add_argument('pack');ap.add_argument('--output',required=True);args=ap.parse_args()
    data=pathlib.Path(args.pack).read_bytes(); toc=struct.unpack_from('<Q',data,len(data)-8)[0]; count=struct.unpack_from('<I',data,len(data)-12)[0]
    p=toc
    for _ in range(count):
        n=struct.unpack_from('<H',data,p)[0];p+=2+n+28
    gap=data[p:-0x41c];assert gap[:10]==b'HashVer1.3'
    buckets,entries,extra,length=struct.unpack_from('<4I',gap,16)
    # EXE writer emits a 32-byte header then the transformed compressed stream.
    # length is stream length, not the length beyond an invented 0x44 header.
    stream=transform(gap[32:],length)
    assert transform(stream,length,False)==gap[32:]
    raw=decompress(stream[:length]); pos=0; groups=[];records=[]
    for bucket in range(buckets):
        n=struct.unpack_from('<H',raw,pos)[0];pos+=2;groups.append(n)
        for _ in range(n):
            namelen=struct.unpack_from('<H',raw,pos)[0];pos+=2
            name=raw[pos:pos+namelen];pos+=namelen
            value,attr=struct.unpack_from('<QI',raw,pos);pos+=12
            records.append(dict(bucket=bucket,name=name.decode('cp932',errors='backslashreplace'),rawName=name.hex(),value=value,attribute=attr))
    report=dict(archive=args.pack,gapOffset=p,gapLength=len(gap),headerSize=32,buckets=buckets,entries=entries,extra=extra,streamLength=length,streamBytesAvailable=len(gap)-32,decodedLength=len(raw),tableEnd=pos,remaining=len(raw)-pos,recordCount=len(records),groups=groups,records=records,decodedSha256=hashlib.sha256(raw).hexdigest(),cipherRoundtrip=True)
    out=pathlib.Path(args.output);out.mkdir(parents=True,exist_ok=False)
    (out/'decoded.bin').write_bytes(raw);(out/'stream.bin').write_bytes(stream);(out/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k not in ('groups','records')},ensure_ascii=False,indent=2));print(records[:5])
if __name__=='__main__':main()
