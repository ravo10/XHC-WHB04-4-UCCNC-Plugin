# Generates src/WHB04B_pendant/app.ico (original handwheel icon).
# Usage: python3 tools/make_icon.py src/WHB04B_pendant/app.ico docs/img/icon.png
import math, sys
from PIL import Image, ImageDraw
S=1024
img=Image.new("RGBA",(S,S),(0,0,0,0))
d=ImageDraw.Draw(img)
orange=(232,89,12,255); dark=(43,47,54,255); white=(255,255,255,255); light=(255,214,186,255)
# rounded square background
d.rounded_rectangle([24,24,S-24,S-24],radius=200,fill=orange)
cx=cy=S//2
R=380
# outer ring with tick marks (graduated dial)
d.ellipse([cx-R,cy-R,cx+R,cy+R],fill=white)
for i in range(50):
    a=2*math.pi*i/50
    long_=(i%5==0)
    r1=R-18; r2=R-(78 if long_ else 50)
    w=16 if long_ else 9
    d.line([(cx+r1*math.cos(a),cy+r1*math.sin(a)),(cx+r2*math.cos(a),cy+r2*math.sin(a))],fill=dark,width=w)
# inner wheel
r=230
d.ellipse([cx-r,cy-r,cx+r,cy+r],fill=dark)
r2=150
d.ellipse([cx-r2,cy-r2,cx+r2,cy+r2],fill=(70,76,86,255))
# crank knob near the edge of the inner wheel
kx=cx+int(130*math.cos(math.radians(135))); ky=cy+int(130*math.sin(math.radians(135)))
d.ellipse([kx-62,ky-62,kx+62,ky+62],fill=orange)
d.ellipse([kx-30,ky-30,kx+30,ky+30],fill=light)
# zero marker at top
d.polygon([(cx-34,cy-R-4),(cx+34,cy-R-4),(cx,cy-R+58)],fill=orange)
sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)]
img.save(sys.argv[1],sizes=sizes)
img.resize((256,256),Image.LANCZOS).save(sys.argv[2])
