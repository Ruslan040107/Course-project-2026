import glob, os, numpy as np
from scipy.io import wavfile
from PIL import Image, ImageDraw
fs=sorted(glob.glob('*/**/*.wav',recursive=True))
W,H=300,70; cols=3; rows=(len(fs)+cols-1)//cols
S=Image.new('RGB',(cols*W,rows*H),(20,20,20)); d=ImageDraw.Draw(S)
for i,f in enumerate(fs):
    sr,x=wavfile.read(f); x=x/32768.0; ox,oy=(i%cols)*W,(i//cols)*H
    n=len(x); step=max(1,n//W)
    for px in range(W):
        seg=x[px*step:(px+1)*step]
        if len(seg)==0: break
        a=int(seg.max()*H/2.2); b=int(seg.min()*H/2.2)
        d.line([(ox+px,oy+H//2-a),(ox+px,oy+H//2-b)],fill=(90,200,120))
    d.text((ox+3,oy+2),f.replace(os.sep,'/')+' %.2fs'%(n/sr),fill=(255,255,255))
S.save('../waves.png')
