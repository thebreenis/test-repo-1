"""Create Pop Puller's Metro icon. Requires Pillow only when regenerating it."""
from pathlib import Path
from PIL import Image, ImageDraw

SIZE = 1024
im = Image.new('RGBA', (SIZE, SIZE), (35, 8, 48, 255))
d = ImageDraw.Draw(im)
d.rounded_rectangle((96, 96, 928, 928), radius=100, fill=(58, 18, 83, 255))
d.rounded_rectangle((140, 660, 884, 832), radius=32, fill=(0, 162, 176, 255))
d.rounded_rectangle((176, 690, 848, 774), radius=18, fill=(24, 85, 103, 255))
d.polygon([(434, 250), (590, 250), (590, 470), (675, 470), (512, 640), (349, 470), (434, 470)], fill='white')
d.rounded_rectangle((435, 789, 589, 816), radius=12, fill='white')
im.save(Path(__file__).with_name('PopPuller.ico'), format='ICO', sizes=[(16,16), (24,24), (32,32), (48,48), (64,64), (128,128), (256,256)])
