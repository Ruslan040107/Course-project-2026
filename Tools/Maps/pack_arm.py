# Poly Haven ARM map (R = ambient occlusion, G = roughness, B = metallic) -> the URP Lit mask:
# R = metallic, G = occlusion, B = 0, A = smoothness (1 - roughness). PropImport.cs uses it as both
# _MetallicGlossMap (smoothness from its alpha) and _OcclusionMap (green), imported as linear (not sRGB).
# An optional max size scales the map down (fetch_map_assets.py uses 512).
#   python -I pack_arm.py <arm.jpg> <out.png> [max size, e.g. 512]
import sys
from PIL import Image, ImageOps, ImageStat

src, dst = sys.argv[1], sys.argv[2]
img = Image.open(src).convert('RGB')
if len(sys.argv) > 3 and max(img.size) > int(sys.argv[3]):
    k = int(sys.argv[3]) / max(img.size)
    img = img.resize((max(1, round(img.width * k)), max(1, round(img.height * k))), Image.LANCZOS)
ao, rough, metal = img.split()
# an empty AO channel (metal_trash_can: all 0) would make the whole model black: no occlusion then
if ImageStat.Stat(ao).mean[0] < 8:
    ao = Image.new('L', ao.size, 255)
    print('AO channel empty, using white:', src)
Image.merge('RGBA', (metal, ao, Image.new('L', ao.size, 0), ImageOps.invert(rough))).save(dst, optimize=True)
print('packed', dst, ao.size)
