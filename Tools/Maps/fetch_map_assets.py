# Downloads and converts the assets of the maps Borgo (Mediterranean town) and Porto (industrial port).
# Everything is CC0 (see CREDITS.md); this file is the list of where each file came from.
#   - ambientCG textures, 1K-JPG zips: only <ID>_Color.jpg and <ID>_NormalGL.jpg are kept -> Assets/Textures/Map/<ID>/
#   - Poly Haven skies, 2k .hdr                                                    -> Assets/Textures/Sky/
#   - Poly Haven props, glTF 1k: FBX made in Blender (gltf2fbx.py), the colour, normal and emission textures
#     beside it, the ARM map packed for URP (pack_arm.py) and <slug>.json with the materials -> Assets/Models/Props/<slug>/
#     The URP materials and the import settings are made in Unity: Tools > Maps > Set Up Map Assets (PropImport.cs).
#
#   python -I Tools/Maps/fetch_map_assets.py <download dir> <project dir> [--only textures,skies,props] [--ids a,b] [--blender <exe>]
#
# Downloads that are already there (same md5 / size) are not fetched again. The download dir is kept apart from the
# project: only the files listed below are copied into Assets.
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import urllib.parse
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
BLENDER = r'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
AGENT = 'Mozilla/5.0 (HSE course project; map asset fetch)'
MASK_SIZE = 512     # the packed metallic / occlusion / smoothness maps of the props

# ambientCG id, map that uses it, size of the 1K-JPG zip in bytes; url https://ambientcg.com/get?file=<ID>_1K-JPG.zip
TEXTURES = [
    ('PaintedPlaster018', 'Borgo', 8851753),
    ('PaintedPlaster013', 'Borgo', 8856518),
    ('PaintedPlaster006', 'Borgo', 8959193),
    ('RoofingTiles014A', 'Borgo', 5001373),
    ('PavingStones119', 'Borgo', 8986871),
    ('PavingStones151', 'Borgo', 8736158),
    ('Concrete012', 'Borgo', 8171336),
    ('Planks039', 'Borgo', 4109128),
    ('PaintedWood009A', 'Borgo', 7128574),
    ('PaintedWood008A', 'Borgo', 7366682),
    ('Asphalt031', 'Porto', 8699189),
    ('Road006', 'Porto', 6686921),
    ('Concrete031', 'Porto', 7823020),
    ('Concrete044D', 'Porto', 7540334),
    ('CorrugatedSteel002', 'Porto', 5614977),
    ('CorrugatedSteel004', 'Porto', 4913427),
    ('CorrugatedSteel006A', 'Porto', 5397011),
    ('CorrugatedSteel007A', 'Porto', 5091909),
    ('Metal022', 'Porto', 9159177),
    ('DiamondPlate008C', 'Porto', 7925288),
    ('PaintedMetal016', 'Porto', 9936183),
    ('PaintedMetal007', 'Porto', 8307261),
]

# Poly Haven HDRIs (pure sky, 2k): file name, url, md5
SKIES = [
    ('kloofendal_48d_partly_cloudy_puresky_2k.hdr',
     'https://dl.polyhaven.org/file/ph-assets/HDRIs/hdr/2k/kloofendal_48d_partly_cloudy_puresky_2k.hdr',
     '2eba3a4d7eeb23cbfbeca364c97e7980'),
    ('sunflowers_puresky_2k.hdr',
     'https://dl.polyhaven.org/file/ph-assets/HDRIs/hdr/2k/sunflowers_puresky_2k.hdr',
     '2609ea973b9b1a3cdc4234961606ebc2'),
]

# Poly Haven models, glTF 1k: slug -> (authors, [(path inside the glTF folder, url, md5), ...]).
# The .bin and the textures are at other urls than the .gltf: these are the "include" urls of
# https://api.polyhaven.com/files/<slug>. modular_pipes (47k triangles) is left out on purpose.
PROPS = {
    'covered_car': ('MP', [
        ('covered_car_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/covered_car/covered_car_1k.gltf', 'c38cd2ddf3fe2abe7ed8df2fd6020b84'),
        ('covered_car.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/covered_car/covered_car.bin', '38f16c2c092dff1191ac5383acaf2ca0'),
        ('textures/covered_car_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/covered_car/covered_car_arm_1k.jpg', '62f9a8c8719ce46ee5464462ec6ed6fc'),
        ('textures/covered_car_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/covered_car/covered_car_diff_1k.jpg', '61c107225ca1cbd523d6d6e9c1775f22'),
        ('textures/covered_car_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/covered_car/covered_car_nor_gl_1k.jpg', 'afc43e8dd74213483a454cdb00f0f541'),
    ]),
    'wine_barrel_01': ('James Ray Cock', [
        ('wine_barrel_01_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/wine_barrel_01/wine_barrel_01_1k.gltf', '5397e6d959dd3ef20d1fc14436d0eb70'),
        ('textures/wine_barrel_01_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wine_barrel_01/wine_barrel_01_arm_1k.jpg', '2f0cc81ac07fdbe8c22927feac722927'),
        ('textures/wine_barrel_01_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wine_barrel_01/wine_barrel_01_diff_1k.jpg', 'fdea9538acf01a04a61626919a84b3fb'),
        ('textures/wine_barrel_01_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wine_barrel_01/wine_barrel_01_nor_gl_1k.jpg', '6b3ce558fa74d3760fa0192dfc4564a3'),
        ('wine_barrel_01.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/wine_barrel_01/wine_barrel_01.bin', '84f578460020912056a1e3fbe3ec1b97'),
    ]),
    'street_lamp_01': ('Josh Dean', [
        ('street_lamp_01_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/street_lamp_01/street_lamp_01_1k.gltf', 'd29d1fc30bbdad99a58371754f144e65'),
        ('street_lamp_01.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/street_lamp_01/street_lamp_01.bin', '324bc6295e3962322514ddcfc7037b23'),
        ('textures/street_lamp_01_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/street_lamp_01/street_lamp_01_arm_1k.jpg', '63d980174db10cf22a1b175743c465c9'),
        ('textures/street_lamp_01_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/street_lamp_01/street_lamp_01_diff_1k.jpg', '82efb7531a25cda3ef8af2aff8ecccf7'),
        ('textures/street_lamp_01_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/street_lamp_01/street_lamp_01_nor_gl_1k.jpg', 'c27d8102c7e851a8f0f654bb959b9d67'),
    ]),
    'street_lamp_02': ('Josh Dean', [
        ('street_lamp_02_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/street_lamp_02/street_lamp_02_1k.gltf', '94023fbd7e6392d9276d91ca36c00144'),
        ('street_lamp_02.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/street_lamp_02/street_lamp_02.bin', '5c89269a65d65cdb33d018012f003ef7'),
        ('textures/street_lamp_02_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/street_lamp_02/street_lamp_02_arm_1k.jpg', 'd4d49b0153762ebe5ccde8e87af87dd5'),
        ('textures/street_lamp_02_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/street_lamp_02/street_lamp_02_diff_1k.jpg', '381097a4c700b86b1c8f7e0d115b1a0a'),
        ('textures/street_lamp_02_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/street_lamp_02/street_lamp_02_nor_gl_1k.jpg', '39389c9a5c2c467ea559ac3ad3a03218'),
    ]),
    'painted_wooden_bench': ('Kirill Sannikov', [
        ('painted_wooden_bench_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/painted_wooden_bench/painted_wooden_bench_1k.gltf', '784a49250144aa5299a93c1f3b6968d7'),
        ('painted_wooden_bench.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/4k/painted_wooden_bench/painted_wooden_bench.bin', 'ed331b50f955852f007313c1a75c2e2d'),
        ('textures/painted_wooden_bench_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/painted_wooden_bench/painted_wooden_bench_arm_1k.jpg', '754ecc7f47b1e5d032e45e22334954f1'),
        ('textures/painted_wooden_bench_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/painted_wooden_bench/painted_wooden_bench_diff_1k.jpg', '60c7d0f34f52b1003345d318e35f468d'),
        ('textures/painted_wooden_bench_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/painted_wooden_bench/painted_wooden_bench_nor_gl_1k.jpg', '08b74bebcff262c14beae35a4f7b3405'),
    ]),
    'outdoor_table_chair_set_01': ('James Ray Cock', [
        ('outdoor_table_chair_set_01_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/outdoor_table_chair_set_01/outdoor_table_chair_set_01_1k.gltf', 'e5f81fc86b52135f5a5897a51b859b07'),
        ('outdoor_table_chair_set_01.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/4k/outdoor_table_chair_set_01/outdoor_table_chair_set_01.bin', '8596ca40855bc417ac5248a54bd85990'),
        ('textures/outdoor_table_chair_set_01_chair_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/outdoor_table_chair_set_01/outdoor_table_chair_set_01_chair_arm_1k.jpg', '52352847c3ce824803a9d500eec5614d'),
        ('textures/outdoor_table_chair_set_01_chair_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/outdoor_table_chair_set_01/outdoor_table_chair_set_01_chair_diff_1k.jpg', 'cb6d4004ad957a6b89aabe4c8dddd6fd'),
        ('textures/outdoor_table_chair_set_01_chair_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/outdoor_table_chair_set_01/outdoor_table_chair_set_01_chair_nor_gl_1k.jpg', 'ac08f5c3335c94e1dca986383265c23a'),
        ('textures/outdoor_table_chair_set_01_table_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/outdoor_table_chair_set_01/outdoor_table_chair_set_01_table_arm_1k.jpg', 'cf5aa95ce8b83991eaf5048eacea0611'),
        ('textures/outdoor_table_chair_set_01_table_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/outdoor_table_chair_set_01/outdoor_table_chair_set_01_table_diff_1k.jpg', 'afd8871dffcad88170a27b5fce6e3c7d'),
        ('textures/outdoor_table_chair_set_01_table_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/outdoor_table_chair_set_01/outdoor_table_chair_set_01_table_nor_gl_1k.jpg', 'd284647b39e4ecda93b3ea85f40dc2d7'),
    ]),
    'planter_box_02': ('James Ray Cock', [
        ('planter_box_02_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/planter_box_02/planter_box_02_1k.gltf', '8b5852d5b04c044895afd64cdcb30577'),
        ('planter_box_02.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/planter_box_02/planter_box_02.bin', '436a7737e128fa0736c4318834bf675a'),
        ('textures/planter_box_02_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/planter_box_02/planter_box_02_arm_1k.jpg', '1a35f166fda4a2b2846b9d2b3bc4181d'),
        ('textures/planter_box_02_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/planter_box_02/planter_box_02_diff_1k.jpg', '96fa881a3ac88e033669a2ad8ca20b6c'),
        ('textures/planter_box_02_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/planter_box_02/planter_box_02_nor_gl_1k.jpg', '9348a31f18fd4136a74ae7e7d5e7ac11'),
    ]),
    'planter_pot_clay': ('Amal Kumar', [
        ('planter_pot_clay_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/planter_pot_clay/planter_pot_clay_1k.gltf', '1c78fcfd6144747c97e2b822a6229c0d'),
        ('planter_pot_clay.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/4k/planter_pot_clay/planter_pot_clay.bin', '93aa55f6378a47c3e2d03eb5b98da8b0'),
        ('textures/planter_pot_clay_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/planter_pot_clay/planter_pot_clay_arm_1k.jpg', 'ec397ae29f1f52292cbc9482037c11c4'),
        ('textures/planter_pot_clay_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/planter_pot_clay/planter_pot_clay_diff_1k.jpg', '6823eb18ca73c8cdcb3cbc04704c443b'),
        ('textures/planter_pot_clay_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/planter_pot_clay/planter_pot_clay_nor_gl_1k.jpg', '269b48d6a5038e97acf9d670b7381b3a'),
    ]),
    'antique_ceramic_vase_01': ('James Ray Cock', [
        ('antique_ceramic_vase_01_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/antique_ceramic_vase_01/antique_ceramic_vase_01_1k.gltf', 'b5a82c883c3a29b54eccd2f8400bd479'),
        ('antique_ceramic_vase_01.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/antique_ceramic_vase_01/antique_ceramic_vase_01.bin', '0758bc04ce9826b1ae61c931aed19715'),
        ('textures/antique_ceramic_vase_01_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/antique_ceramic_vase_01/antique_ceramic_vase_01_arm_1k.jpg', 'ca1d7269913774a351bdf10058d7f06f'),
        ('textures/antique_ceramic_vase_01_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/antique_ceramic_vase_01/antique_ceramic_vase_01_diff_1k.jpg', 'a3de8212967f9e230364c5948e1581a5'),
        ('textures/antique_ceramic_vase_01_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/antique_ceramic_vase_01/antique_ceramic_vase_01_nor_gl_1k.jpg', '07fef9d384b8e73d1b03604103ed0b65'),
    ]),
    'large_castle_door': ('Tina', [
        ('large_castle_door_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/large_castle_door/large_castle_door_1k.gltf', '40c7debfb0e9f51826b294d642bc0bfe'),
        ('large_castle_door.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/4k/large_castle_door/large_castle_door.bin', 'ed0cee2626677b1a520de7bd868d09cc'),
        ('textures/large_castle_door_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/large_castle_door/large_castle_door_arm_1k.jpg', 'e742bcceba429ca946edc788dac922ff'),
        ('textures/large_castle_door_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/large_castle_door/large_castle_door_diff_1k.jpg', '641c090895f68f3be02f8db278385174'),
        ('textures/large_castle_door_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/large_castle_door/large_castle_door_nor_gl_1k.jpg', '4a0e452157d95c0d18a2f79429ec471d'),
    ]),
    'wooden_lantern_01': ('James Ray Cock', [
        ('wooden_lantern_01_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/wooden_lantern_01/wooden_lantern_01_1k.gltf', 'e31edca6461023e076183cb74721b0b9'),
        ('textures/wooden_lantern_01_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wooden_lantern_01/wooden_lantern_01_arm_1k.jpg', '87b5bdc9f3d5853caef38a00589541ae'),
        ('textures/wooden_lantern_01_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wooden_lantern_01/wooden_lantern_01_diff_1k.jpg', '8f9f537ab312b1ec85acb1f5aaf7ad17'),
        ('textures/wooden_lantern_01_glass_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wooden_lantern_01/wooden_lantern_01_glass_arm_1k.jpg', '890c1dd6ad9c37521f9f0a0d9d08fba2'),
        ('textures/wooden_lantern_01_glass_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wooden_lantern_01/wooden_lantern_01_glass_diff_1k.jpg', 'c1e45d48f344f86726d02c81679af6a4'),
        ('textures/wooden_lantern_01_glass_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wooden_lantern_01/wooden_lantern_01_glass_nor_gl_1k.jpg', '38549e36957f90dc61587010c3a8ff3a'),
        ('textures/wooden_lantern_01_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wooden_lantern_01/wooden_lantern_01_nor_gl_1k.jpg', '9db1fa975880b3cf6da5e6c30853e0cf'),
        ('wooden_lantern_01.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/wooden_lantern_01/wooden_lantern_01.bin', '3784667126369ce5c21db3c7436409cb'),
    ]),
    'wooden_crate_01': ('James Ray Cock', [
        ('wooden_crate_01_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/wooden_crate_01/wooden_crate_01_1k.gltf', '33df4d2ae00186d4b64e878466c9e22e'),
        ('textures/wooden_crate_01_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wooden_crate_01/wooden_crate_01_arm_1k.jpg', '67c4aeb1154af98f95516b21ef62291c'),
        ('textures/wooden_crate_01_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wooden_crate_01/wooden_crate_01_diff_1k.jpg', '266e0462d06e77c5745f66e89820544f'),
        ('textures/wooden_crate_01_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wooden_crate_01/wooden_crate_01_nor_gl_1k.jpg', 'e31626a1fcf6f7062c2ffd9fc982b942'),
        ('wooden_crate_01.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/wooden_crate_01/wooden_crate_01.bin', '8318f76b0c24b5538ce23051dbcb79a0'),
    ]),
    'wooden_crate_02': ('James Ray Cock, Jurita Burger', [
        ('wooden_crate_02_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/wooden_crate_02/wooden_crate_02_1k.gltf', 'a69eb62fd20e6badb6bb56f052930e06'),
        ('textures/wooden_crate_02_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wooden_crate_02/wooden_crate_02_arm_1k.jpg', 'a7537612dbfb7fd25e756074eec5624e'),
        ('textures/wooden_crate_02_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wooden_crate_02/wooden_crate_02_diff_1k.jpg', '45bca8f49da9d541e06bc926ab001c81'),
        ('textures/wooden_crate_02_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wooden_crate_02/wooden_crate_02_nor_gl_1k.jpg', '314226b31765168cd8070898f8a7e18e'),
        ('wooden_crate_02.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/wooden_crate_02/wooden_crate_02.bin', '08d98507231cdeda1070d1a2d0646254'),
    ]),
    'Barrel_01': ('Jorge Camacho', [
        ('Barrel_01_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/Barrel_01/Barrel_01_1k.gltf', '45c109401126ea3e5df2fe2c6b6f6d39'),
        ('Barrel_01.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/Barrel_01/Barrel_01.bin', 'e9f1036a183a1747aea6d40c556d7410'),
        ('textures/Barrel_01_explosive_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/Barrel_01/Barrel_01_explosive_arm_1k.jpg', 'ccfe5da162b909fe98f4780e41cc1233'),
        ('textures/Barrel_01_explosive_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/Barrel_01/Barrel_01_explosive_diff_1k.jpg', 'a36cd738a17cab52dc4c264a03b2848b'),
        ('textures/Barrel_01_explosive_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/Barrel_01/Barrel_01_explosive_nor_gl_1k.jpg', 'f49be8d1ab465c944380b9d565c6ab47'),
    ]),
    'barrel_03': ('Serhii Khromov', [
        ('barrel_03_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/barrel_03/barrel_03_1k.gltf', '204eec2159a38a1dc4b826ef255f9468'),
        ('barrel_03.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/4k/barrel_03/barrel_03.bin', '6993587e3ef8e0603334e615ab292288'),
        ('textures/barrel_03_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/barrel_03/barrel_03_arm_1k.jpg', '3520338e30b4d257745db34848098688'),
        ('textures/barrel_03_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/barrel_03/barrel_03_diff_1k.jpg', '6e77dc1af60c48787fd80dbbcf125040'),
        ('textures/barrel_03_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/barrel_03/barrel_03_nor_gl_1k.jpg', 'fd181365eb7397d55773987d145f0420'),
    ]),
    'Barrel_02': ('Jorge Camacho', [
        ('Barrel_02_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/Barrel_02/Barrel_02_1k.gltf', 'f95584ab7cc325d8be4884ca2935e019'),
        ('Barrel_02.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/Barrel_02/Barrel_02.bin', '7340fa43610b7a983a1b352574427b64'),
        ('textures/Barrel_02_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/Barrel_02/Barrel_02_arm_1k.jpg', '2c4ab82215e0bc4d0b173c79bc18f15a'),
        ('textures/Barrel_02_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/Barrel_02/Barrel_02_diff_1k.jpg', '7d879597fa5cae9ef9095b259ccaf9fb'),
        ('textures/Barrel_02_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/Barrel_02/Barrel_02_nor_gl_1k.jpg', 'fa1022a8d9206af8c1997db0a9597170'),
    ]),
    'wooden_military_crate': ('Prabhjinder Singh', [
        ('wooden_military_crate_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/wooden_military_crate/wooden_military_crate_1k.gltf', 'a6dc6b8e8519a115e8481d9a1c9525cf'),
        ('textures/wooden_military_crate_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wooden_military_crate/wooden_military_crate_arm_1k.jpg', 'b8f165142d290078ffb08b608cec9d48'),
        ('textures/wooden_military_crate_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wooden_military_crate/wooden_military_crate_diff_1k.jpg', '042ee9d280238702cde679ea00bb37ec'),
        ('textures/wooden_military_crate_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/wooden_military_crate/wooden_military_crate_nor_gl_1k.jpg', '768fcbb54910fbad95abfb6bd90901d3'),
        ('wooden_military_crate.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/4k/wooden_military_crate/wooden_military_crate.bin', 'fb5dbf4aed842eb45f2e18e62683d34a'),
    ]),
    'old_military_crate': ('Jack Mava', [
        ('old_military_crate_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/old_military_crate/old_military_crate_1k.gltf', '4c83b9887a0db6ecd5af18293012c3f0'),
        ('old_military_crate.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/old_military_crate/old_military_crate.bin', '4aa204ebcc401c38206677a808aebea6'),
        ('textures/old_military_crate_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/old_military_crate/old_military_crate_arm_1k.jpg', '5e877bd1644fe490e9450794aadc3dd7'),
        ('textures/old_military_crate_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/old_military_crate/old_military_crate_diff_1k.jpg', 'a679925cb14dbb3ffc39a5d152d23114'),
        ('textures/old_military_crate_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/old_military_crate/old_military_crate_nor_gl_1k.jpg', '4403c6773bcc7c0182fac32e750c7231'),
    ]),
    'metal_trash_can': ('GurJas Studios', [
        ('metal_trash_can_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/metal_trash_can/metal_trash_can_1k.gltf', 'c322802832a61ad7b17711dfbbc52e6b'),
        ('metal_trash_can.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/metal_trash_can/metal_trash_can.bin', 'fbde1679a66ea644223b97ebc8336a77'),
        ('textures/metal_trash_can_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/metal_trash_can/metal_trash_can_arm_1k.jpg', 'af676572c13fae2bb584538f03175efc'),
        ('textures/metal_trash_can_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/metal_trash_can/metal_trash_can_diff_1k.jpg', '55d375d8d51b268b6ed8a4308155959c'),
        ('textures/metal_trash_can_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/metal_trash_can/metal_trash_can_nor_gl_1k.jpg', 'fa6db04e021f6335ae2d9ad501120cae'),
        ('textures/metal_trash_can_rust_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/metal_trash_can/metal_trash_can_rust_arm_1k.jpg', 'ed9f635917cde411f235d4585c90b310'),
        ('textures/metal_trash_can_rust_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/metal_trash_can/metal_trash_can_rust_diff_1k.jpg', '8b90447d250cb790ca537748a2b16691'),
        ('textures/metal_trash_can_rust_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/metal_trash_can/metal_trash_can_rust_nor_gl_1k.jpg', '25187818d1236815d4a34fd743cadc29'),
    ]),
    'rollershutter_door': ('MP', [
        ('rollershutter_door_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/rollershutter_door/rollershutter_door_1k.gltf', 'e8ad4c4ea26db990d32301cd1f7481cf'),
        ('rollershutter_door.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/rollershutter_door/rollershutter_door.bin', '9df2ea3372b84dca7959d81cd5818d29'),
        ('textures/rollershutter_door_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/rollershutter_door/rollershutter_door_arm_1k.jpg', '5fc759f9776f232e6039ec5f847073df'),
        ('textures/rollershutter_door_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/rollershutter_door/rollershutter_door_diff_1k.jpg', '2903cbe74c5d9d434951ec8625725458'),
        ('textures/rollershutter_door_graffiti_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/rollershutter_door/rollershutter_door_graffiti_diff_1k.jpg', '1985c43406b33a7a874ea78041dd44db'),
        ('textures/rollershutter_door_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/rollershutter_door/rollershutter_door_nor_gl_1k.jpg', 'bd6f336490c10bdb6ca1a527981054a3'),
    ]),
    'modular_industrial_pipes_01': ('Jorge Camacho', [
        ('modular_industrial_pipes_01_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/modular_industrial_pipes_01/modular_industrial_pipes_01_1k.gltf', 'cb30fdf4d471374ba439bfd5a0c8105d'),
        ('modular_industrial_pipes_01.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/modular_industrial_pipes_01/modular_industrial_pipes_01.bin', 'a18d063dc719e19b9c20d2f8c0a8bec4'),
        ('textures/modular_industrial_pipes_01_group01_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/modular_industrial_pipes_01/modular_industrial_pipes_01_group01_arm_1k.jpg', '2ab8f10dc9ca832af70cd48183281f97'),
        ('textures/modular_industrial_pipes_01_group01_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/modular_industrial_pipes_01/modular_industrial_pipes_01_group01_diff_1k.jpg', '28678434837d64476cb8d75d0314166e'),
        ('textures/modular_industrial_pipes_01_group01_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/modular_industrial_pipes_01/modular_industrial_pipes_01_group01_nor_gl_1k.jpg', 'a5d4deb3ea6772c0af1275edeab7dc94'),
        ('textures/modular_industrial_pipes_01_group02_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/modular_industrial_pipes_01/modular_industrial_pipes_01_group02_arm_1k.jpg', '088f90ba0d60c13ff0d6fc43cf734372'),
        ('textures/modular_industrial_pipes_01_group02_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/modular_industrial_pipes_01/modular_industrial_pipes_01_group02_diff_1k.jpg', 'd3d951f471cfcd15e89cedcd43d28606'),
        ('textures/modular_industrial_pipes_01_group02_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/modular_industrial_pipes_01/modular_industrial_pipes_01_group02_nor_gl_1k.jpg', '282735667b3cd6e2d0ef058f2c260740'),
    ]),
    'hanging_industrial_lamp': ('Kuutti Siitonen', [
        ('hanging_industrial_lamp_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/hanging_industrial_lamp/hanging_industrial_lamp_1k.gltf', '59baa7ef17a7696ac26bc5bd70fbb3df'),
        ('hanging_industrial_lamp.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/4k/hanging_industrial_lamp/hanging_industrial_lamp.bin', '7148574f0db05abe68f7e51565abe2ce'),
        ('textures/hanging_industrial_lamp_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/hanging_industrial_lamp/hanging_industrial_lamp_arm_1k.jpg', '077f309d4ed9d38b19be1aa75462771e'),
        ('textures/hanging_industrial_lamp_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/hanging_industrial_lamp/hanging_industrial_lamp_diff_1k.jpg', '9931801f2ff2533c891105c7a41af39d'),
        ('textures/hanging_industrial_lamp_emissive_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/hanging_industrial_lamp/hanging_industrial_lamp_emissive_1k.jpg', 'a9e170515931ba022ec6436e188d360b'),
        ('textures/hanging_industrial_lamp_glass_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/hanging_industrial_lamp/hanging_industrial_lamp_glass_nor_gl_1k.jpg', '2e64e3e249137658baaa7cde64ad4def'),
        ('textures/hanging_industrial_lamp_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/hanging_industrial_lamp/hanging_industrial_lamp_nor_gl_1k.jpg', '6820f01127e747636024838f0bde0494'),
    ]),
    'security_light': ('Maximilian Schuster', [
        ('security_light_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/security_light/security_light_1k.gltf', '727025a49f3ccbbf14905b577190932b'),
        ('security_light.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/security_light/security_light.bin', '396e444f26305c6bc7ad0747ee3f899e'),
        ('textures/security_light_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/security_light/security_light_arm_1k.jpg', '9f827fc14a511d7061a713ae2f80988d'),
        ('textures/security_light_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/security_light/security_light_diff_1k.jpg', 'b7c8f67d7a5b0db24d578eb9154fd1bf'),
        ('textures/security_light_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/security_light/security_light_nor_gl_1k.jpg', '44772c73720886e5d762c3d7ef09eb03'),
    ]),
    'industrial_wall_lamp': ('Kuutti Siitonen', [
        ('industrial_wall_lamp_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/industrial_wall_lamp/industrial_wall_lamp_1k.gltf', '3e6245e63698d5b7aef862280ec77d74'),
        ('industrial_wall_lamp.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/4k/industrial_wall_lamp/industrial_wall_lamp.bin', '86407c4250e46ad39c5a86e1dc1034f9'),
        ('textures/industrial_wall_lamp_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/industrial_wall_lamp/industrial_wall_lamp_arm_1k.jpg', '10de18cfac3070f42bc4fcd79d3993f4'),
        ('textures/industrial_wall_lamp_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/industrial_wall_lamp/industrial_wall_lamp_diff_1k.jpg', 'fec1b0755dccb02b19b6a386169460cc'),
        ('textures/industrial_wall_lamp_emissive_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/industrial_wall_lamp/industrial_wall_lamp_emissive_1k.jpg', '32a123a424debe690c0bde4dc0ddeab0'),
        ('textures/industrial_wall_lamp_glass_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/industrial_wall_lamp/industrial_wall_lamp_glass_arm_1k.jpg', '8e7922054b07fcf58beb8009f88db049'),
        ('textures/industrial_wall_lamp_glass_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/industrial_wall_lamp/industrial_wall_lamp_glass_diff_1k.jpg', '3b92d16c0f316b2648e2c4dbd0ed4776'),
        ('textures/industrial_wall_lamp_glass_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/industrial_wall_lamp/industrial_wall_lamp_glass_nor_gl_1k.jpg', '05d60c2d147a240b347023e01df7129f'),
        ('textures/industrial_wall_lamp_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/industrial_wall_lamp/industrial_wall_lamp_nor_gl_1k.jpg', 'e1b1f9f223e26c4631418a338f1fe08f'),
    ]),
    'utility_box_02': ('James Ray Cock', [
        ('utility_box_02_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/utility_box_02/utility_box_02_1k.gltf', 'e73af61886eedd147d66e7e295543142'),
        ('textures/utility_box_02_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/utility_box_02/utility_box_02_arm_1k.jpg', '062e9ea1df88d177e332b1595bb27a12'),
        ('textures/utility_box_02_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/utility_box_02/utility_box_02_diff_1k.jpg', '119987b82e0faac169e2109ff5961743'),
        ('textures/utility_box_02_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/utility_box_02/utility_box_02_nor_gl_1k.jpg', '6704e7af3075eaf58ff7b7fb791f6610'),
        ('utility_box_02.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/utility_box_02/utility_box_02.bin', 'bff3e7d9375d8c5ea5723ad007e45258'),
    ]),
    'worn_metal_rack': ('Luca B', [
        ('worn_metal_rack_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/worn_metal_rack/worn_metal_rack_1k.gltf', '1e6ab13765080f11751b2843e2e4c321'),
        ('textures/worn_metal_rack_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/worn_metal_rack/worn_metal_rack_arm_1k.jpg', '32a956dcffe9745b1d95970b0bf58e5a'),
        ('textures/worn_metal_rack_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/worn_metal_rack/worn_metal_rack_diff_1k.jpg', '3f69d57964eb7e67899611de63ba6588'),
        ('textures/worn_metal_rack_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/worn_metal_rack/worn_metal_rack_nor_gl_1k.jpg', '6e9c6ca272eff7828cc0afd88960adb1'),
        ('worn_metal_rack.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/worn_metal_rack/worn_metal_rack.bin', 'd7e62674d4846c5899323efd9c549f55'),
    ]),
    'portable_generator': ('James Ray Cock', [
        ('portable_generator_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/portable_generator/portable_generator_1k.gltf', '13c2380487daae6726443b9f38dd7e53'),
        ('portable_generator.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/portable_generator/portable_generator.bin', '81b2f4c4c6d0b0d7388ae9aa1123563a'),
        ('textures/portable_generator_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/portable_generator/portable_generator_arm_1k.jpg', '7dbb8b9f0aa4e9d3ce6ad37e0326299f'),
        ('textures/portable_generator_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/portable_generator/portable_generator_diff_1k.jpg', '3d572e1716148ca5d641d8a199fe9192'),
        ('textures/portable_generator_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/portable_generator/portable_generator_nor_gl_1k.jpg', '89a51a982488c21eddec2c6e02249591'),
        ('textures/portable_generator_spec_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/portable_generator/portable_generator_spec_1k.jpg', 'b3044dbc984897ed902bd47a450f742f'),
    ]),
    'propane_tank': ('Slinc', [
        ('propane_tank_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/propane_tank/propane_tank_1k.gltf', '4579fb704d4d97e1aca048c126f6d921'),
        ('propane_tank.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/4k/propane_tank/propane_tank.bin', 'bb8e4a72c3f655bfa3dec7456a343451'),
        ('textures/propane_tank_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/propane_tank/propane_tank_arm_1k.jpg', '23773169952447a34c90944eb17563a0'),
        ('textures/propane_tank_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/propane_tank/propane_tank_diff_1k.jpg', '935d42c466adae18710e1f0fc79b306b'),
        ('textures/propane_tank_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/propane_tank/propane_tank_nor_gl_1k.jpg', 'd6f11926f096f3373f091bb35f83ddd3'),
    ]),
    'old_tyre': ('MP', [
        ('old_tyre_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/old_tyre/old_tyre_1k.gltf', 'e29d7aee130a850f2224ac0dd103617b'),
        ('old_tyre.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/4k/old_tyre/old_tyre.bin', 'e29bd9703ad64da8883d8389b0cfb797'),
        ('textures/old_tyre_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/old_tyre/old_tyre_arm_1k.jpg', 'c9256d0d7cac005512cba4744ab29ab4'),
        ('textures/old_tyre_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/old_tyre/old_tyre_diff_1k.jpg', 'f52b712b8373b1d1a7c46702613f76cb'),
        ('textures/old_tyre_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/old_tyre/old_tyre_nor_gl_1k.jpg', 'e5eb6dd2cf7a8e80813bbc1bc21a6d3d'),
    ]),
    'lifebuoy': ('Hank Kaamura', [
        ('lifebuoy_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/lifebuoy/lifebuoy_1k.gltf', '295742069c8a9e56260b769322996e5b'),
        ('lifebuoy.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/lifebuoy/lifebuoy.bin', '4e38cfbae96b6bdfd4ca4ddad06888dd'),
        ('textures/lifebuoy_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/lifebuoy/lifebuoy_arm_1k.jpg', 'a00a461982827a2faaa0c6d940740d51'),
        ('textures/lifebuoy_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/lifebuoy/lifebuoy_diff_1k.jpg', 'c956bce9e530911e95771507547e8a6f'),
        ('textures/lifebuoy_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/lifebuoy/lifebuoy_nor_gl_1k.jpg', 'f8a80ee65e4776d228114f37104fbd37'),
    ]),
    'ocean_buoy': ('Mateusz Sadek', [
        ('ocean_buoy_1k.gltf', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/1k/ocean_buoy/ocean_buoy_1k.gltf', 'bb52e28c01f2f1312afaec7b54103025'),
        ('ocean_buoy.bin', 'https://dl.polyhaven.org/file/ph-assets/Models/gltf/8k/ocean_buoy/ocean_buoy.bin', 'fefe553dc26d2657aaea8d5b8455a137'),
        ('textures/ocean_buoy_arm_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/ocean_buoy/ocean_buoy_arm_1k.jpg', '80422c1b2155584b90fbb88cd388f3ba'),
        ('textures/ocean_buoy_diff_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/ocean_buoy/ocean_buoy_diff_1k.jpg', '00201db15b45d779b6d9d53d965435dc'),
        ('textures/ocean_buoy_emission_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/ocean_buoy/ocean_buoy_emission_1k.jpg', '963fc96fbf5e2a88023518327e691fe9'),
        ('textures/ocean_buoy_nor_gl_1k.jpg', 'https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/ocean_buoy/ocean_buoy_nor_gl_1k.jpg', 'edaecc123cbcd740efa29c757c39518d'),
    ]),
}


def md5_of(path):
    h = hashlib.md5()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def download(url, path, md5=None, size=None):
    if os.path.isfile(path):
        if md5 and md5_of(path) == md5:
            return
        if not md5 and (size is None or os.path.getsize(path) == size):
            return
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + '.part'
    # a connection can end early without an error: the length is checked and the download repeated
    for attempt in range(5):
        print('get', url)
        req = urllib.request.Request(url, headers={'User-Agent': AGENT})
        try:
            with urllib.request.urlopen(req, timeout=180) as r, open(tmp, 'wb') as f:
                length = r.headers.get('Content-Length')
                shutil.copyfileobj(r, f)
        except OSError as e:
            print('  failed:', e)
            continue
        got = os.path.getsize(tmp)
        if length is not None and got != int(length):
            print('  cut off at %d of %s bytes' % (got, length))
            continue
        if md5 and md5_of(tmp) != md5:
            if attempt < 2:
                continue
            print('  WARNING md5 differs (the file was updated on the server?):', url)
        if size is not None and got != size:
            if attempt < 2:
                continue
            print('  WARNING size %d, expected %d: %s' % (got, size, url))
        os.replace(tmp, path)
        return
    raise RuntimeError('download failed: ' + url)


def inside(root, rel):
    # a path from a list or a glTF file must stay inside its folder
    rel = urllib.parse.unquote(rel).replace('\\', '/')
    path = os.path.normpath(os.path.join(root, rel))
    if os.path.isabs(rel) or not path.startswith(os.path.normpath(root) + os.sep):
        raise ValueError('path outside the folder: ' + rel)
    return path


def fetch_textures(dl, project, only_ids=None):
    for tid, _, size in TEXTURES:
        if only_ids and tid not in only_ids:
            continue
        zpath = os.path.join(dl, 'ambientcg', tid + '_1K-JPG.zip')
        download('https://ambientcg.com/get?file=%s_1K-JPG.zip' % tid, zpath, size=size)
        out = os.path.join(project, 'Assets', 'Textures', 'Map', tid)
        os.makedirs(out, exist_ok=True)
        with zipfile.ZipFile(zpath) as z:
            for suffix in ('_Color.jpg', '_NormalGL.jpg'):
                names = [n for n in z.namelist() if n.endswith(suffix) and '/' not in n]
                if not names:
                    raise RuntimeError(tid + ': no *' + suffix + ' in the zip')
                # <ID>_1K-JPG_Color.jpg -> <ID>_Color.jpg
                with z.open(names[0]) as s, open(os.path.join(out, tid + suffix), 'wb') as d:
                    shutil.copyfileobj(s, d)
        print('texture', tid)


def fetch_skies(dl, project):
    out = os.path.join(project, 'Assets', 'Textures', 'Sky')
    os.makedirs(out, exist_ok=True)
    for name, url, md5 in SKIES:
        path = os.path.join(dl, 'polyhaven', 'hdri', name)
        download(url, path, md5)
        shutil.copyfile(path, os.path.join(out, name))
        print('sky', name)


def fetch_props(dl, project, blender, only_slugs=None):
    for slug, (authors, files) in PROPS.items():
        if only_slugs and slug not in only_slugs:
            continue
        src = os.path.join(dl, 'polyhaven', 'models', slug)
        for rel, url, md5 in files:
            download(url, inside(src, rel), md5)
        gltf_path = inside(src, files[0][0])
        with open(gltf_path, encoding='utf-8') as f:
            gltf = json.load(f)

        out = os.path.join(project, 'Assets', 'Models', 'Props', slug)
        os.makedirs(out, exist_ok=True)
        fbx = os.path.join(out, slug + '.fbx')
        res = subprocess.run([blender, '-b', '--factory-startup', '-P', os.path.join(HERE, 'gltf2fbx.py'), '--', gltf_path, fbx],
                             capture_output=True, text=True, encoding='utf-8', errors='replace')
        info = None
        for line in res.stdout.splitlines():
            if line.startswith('PROPINFO '):
                info = json.loads(line[len('PROPINFO '):])
        if res.returncode != 0 or info is None or not os.path.isfile(fbx):
            print(res.stdout[-3000:], res.stderr[-3000:])
            raise RuntimeError(slug + ': the Blender conversion failed')

        def image(tex_info):
            # glTF texture info -> the image file inside the downloaded folder
            if not tex_info:
                return None
            source = gltf['textures'][tex_info['index']]['source']
            return inside(src, gltf['images'][source]['uri'])

        def copy(path):
            if path is None:
                return ''
            shutil.copyfile(path, os.path.join(out, os.path.basename(path)))
            return os.path.basename(path)

        def pack(path):
            # the ARM map (AO, roughness, metallic) -> the URP mask map <name>_ms_1k.png, at MASK_SIZE (a mask PNG
            # does not compress well; half the size of the colour map is plenty for props of a metre or two)
            if path is None:
                return ''
            name = os.path.basename(path).replace('_arm_', '_ms_').rsplit('.', 1)[0] + '.png'
            subprocess.run([sys.executable, '-I', os.path.join(HERE, 'pack_arm.py'), path, os.path.join(out, name), str(MASK_SIZE)],
                           check=True, capture_output=True)
            return name

        materials = []
        for i, m in enumerate(gltf.get('materials', [])):
            pbr = m.get('pbrMetallicRoughness', {})
            ext = m.get('extensions', {})
            emission = list(m.get('emissiveFactor', [0, 0, 0]))
            strength = ext.get('KHR_materials_emissive_strength', {}).get('emissiveStrength', 1.0)
            materials.append({
                'name': info['gltfMaterials'][i],     # unique, as in the FBX
                'baseMap': copy(image(pbr.get('baseColorTexture'))),
                'normalMap': copy(image(m.get('normalTexture'))),
                'maskMap': pack(image(pbr.get('metallicRoughnessTexture') or m.get('occlusionTexture'))),
                'emissionMap': copy(image(m.get('emissiveTexture'))),
                'baseColor': list(pbr.get('baseColorFactor', [1, 1, 1, 1])),
                'emission': [c * strength for c in emission],
                'metallic': pbr.get('metallicFactor', 1.0),
                'roughness': pbr.get('roughnessFactor', 1.0),
                'alphaMode': m.get('alphaMode', 'OPAQUE'),
                'alphaCutoff': m.get('alphaCutoff', 0.5),
                'transmission': ext.get('KHR_materials_transmission', {}).get('transmissionFactor', 0.0),
                'doubleSided': m.get('doubleSided', False),
            })

        manifest = {
            'slug': slug,
            'source': 'https://polyhaven.com/a/' + slug,
            'license': 'CC0',
            'authors': authors,
            'size': info['size'],
            'bottomCentre': info['bottomCentre'],
            'tris': info['tris'],
            'materials': materials,
        }
        with open(os.path.join(out, slug + '.json'), 'w', encoding='utf-8') as f:
            json.dump(manifest, f, indent=2)
        print('prop %s size %s tris %d materials %s' % (slug, info['size'], info['tris'], [m['name'] for m in materials]))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('download_dir')
    ap.add_argument('project_dir')
    ap.add_argument('--only', default='textures,skies,props')
    ap.add_argument('--ids', default='', help='comma separated texture ids / prop slugs (default: all)')
    ap.add_argument('--blender', default=BLENDER)
    a = ap.parse_args()
    dl = os.path.abspath(a.download_dir)
    project = os.path.abspath(a.project_dir)
    parts = a.only.split(',')
    ids = [s for s in a.ids.split(',') if s]
    if 'textures' in parts:
        fetch_textures(dl, project, ids)
    if 'skies' in parts:
        fetch_skies(dl, project)
    if 'props' in parts:
        fetch_props(dl, project, a.blender, ids)


if __name__ == '__main__':
    main()
