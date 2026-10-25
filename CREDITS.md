# Источники ассетов

Все сторонние ассеты — под лицензией **CC0** (общественное достояние): их можно использовать и изменять без ограничений, указание авторства не требуется, но мы его указываем.

## Модели оружия
**Ultimate Gun Pack** — Quaternius (quaternius.com), CC0. Скачано с poly.pizza (зеркало автора).
Доработано в Blender скриптом `Tools/Blender/process.py` (+ `cfg.json`): реальный масштаб, точки рукояти/ствола/выброса гильз, отдельный магазин `Mag`, у дробовиков цевьё `Pump`, у R8 барабан `Cylinder`, глушители, обрезка/добавление деталей, цвета.

| Оружие в игре | Исходная модель (длина, м) |
|---|---|
| Glock-18 | Pistol_5 |
| USP-S | Pistol_6 (+ глушитель) |
| P2000 | Pistol_1, чёрный (0.188) |
| Dual Berettas | Pistol_1, серебро/чёрный (0.217), в игре зеркалится на вторую руку |
| P250 | Pistol_3 (0.19) |
| CZ75-Auto | Pistol_5, светлый затвор, удлинённый магазин + запасной магазин под рамкой (0.206) |
| Five-SeveN | Pistol_6 без глушителя, песочная рамка (0.208) |
| Tec-9 | SubmachineGun_2 без приклада и верхних деталей (0.33) |
| R8 Revolver | Revolver_3 (0.30) |
| Desert Eagle | своя модель (см. ниже) |
| MAC-10 | SubmachineGun_3 без приклада, длинный магазин (0.30) |
| MP9 | SubmachineGun_3 |
| MP7 | SubmachineGun_3, тонкий приклад и передняя рукоять (0.60) |
| UMP-45 | SubmachineGun_2, серый (0.69) |
| MP5-SD | SubmachineGun_2 + глушитель (0.62 + 0.19) |
| PP-Bizon | AssaultRifle_4 без магазина + шнековый магазин под стволом (0.67) |
| P90 | Bullpup_1, укорочен, тёмно-зелёный, магазин сверху (0.50) |
| Nova | Shotgun_2 (0.98) |
| XM1014 | Shotgun_3 (1.00) |
| MAG-7 | Shotgun_ShortStock, коробчатый магазин (0.66) |
| Sawed-Off | Shotgun_SawedOff (0.60) |
| Negev | AssaultRifle_2 без магазина + сошки Bipod + большой короб (1.02) |
| M249 | AssaultRifle2_1 + Bipod + оливковый короб (1.04) |
| Galil AR | AssaultRifle_4 (0.92) |
| FAMAS | Bullpup_1 + ручка для переноски (0.757) |
| AK-47 | AssaultRifle_2 |
| M4A4 | AssaultRifle2_1 |
| M4A1-S | AssaultRifle2_1 + глушитель (0.84 + 0.17) |
| SG 553 | AssaultRifle2_1 без ручки, песочный, + прицел Scope_2 (0.86) |
| AUG | Bullpup_1, оливковый, + Scope_2 (0.79) |
| SSG 08 | SniperRifle_4 (1.09) |
| AWP | SniperRifle_1 |
| SCAR-20 | SniperRifle_5, песочный (1.05) |
| G3SG1 | SniperRifle_3, чёрно-зелёный (1.10) |
| Zeus x27 | своя модель (см. ниже) |
| Нож (бабочка) | своя модель (см. ниже) |

Файлы: `Assets/Resources/WeaponModels/`.

## Свои модели (Blender, без сторонних ассетов)
- **Desert Eagle** — `Tools/Blender/make_deagle.py`: затвор (`Slide`) и магазин (`Mag`) — отдельные детали.
- **Нож-бабочка (балисонг)** — `Tools/Blender/make_butterfly.py`: клинок `Blade` и две рукояти `HandleA`/`HandleB` на своих осях (для анимации раскрытия).
- **Zeus x27** — `Tools/Blender/make_zeus.py`: компактный жёлто-чёрный тазер.

## Персонажи
**Ultimate Modular Men Pack** — Quaternius (quaternius.com), CC0, скачано с poly.pizza. Взяты два персонажа со скелетом и 24 анимациями: Swat (CT) → `Assets/Resources/Characters/CT_Swat.fbx`, Adventurer (T) → `T_Adventurer.fbx`. Хват двумя руками, присед и хитбоксы делаются кодом (`CharacterBody.cs`).

## Звуки выстрелов
**The Free Firearm Sound Library** — Ben Jaszczak, Brian Nelson, Kevin Heras, Matthew Nanney (freefirearmsfx.com, через opengameart.org), CC0.
Нарезаны одиночные выстрелы, 48 кГц моно, фильтр от низкого гула, нормализация; у каждого оружия своя высота тона, эквалайзер и длина хвоста (`Tools/Audio/make_weapon_sounds.py`).

| Оружие в игре | Запись |
|---|---|
| Glock-18 | Walther PPQ |
| USP-S | Ruger Mark III .22 + фильтр «глушителя» |
| P2000 | Walther PPQ (ниже) |
| P250 | Bersa |
| Dual Berettas | Colt 1911 (выше) |
| CZ75-Auto | Bersa (выше, короткий хвост) |
| Tec-9 | Ruger Single Six |
| Five-SeveN | Ruger Mark III |
| R8 Revolver | Smith & Wesson 642 (эхо приглушено) |
| Desert Eagle | Colt 1911 |
| MAC-10, UMP-45 | Carl Gustav M45 |
| MP5-SD | Carl Gustav M45 + фильтр «глушителя» |
| MP9 | ППШ |
| PP-Bizon, P90 | ППШ |
| MP7 | AR-15 (выше) |
| Nova | Benelli Nova |
| Sawed-Off | Mossberg 190 |
| MAG-7 | Winchester Model 12 |
| XM1014 | Charles Daly (помповое ружьё) |
| Negev | AK-47 (ниже) |
| M249 | SKS (ниже) |
| Galil AR | SKS |
| FAMAS | AK-47 (очередь, выше) |
| AK-47 | AK-47 |
| M4A4 | AR-15 |
| M4A1-S | AR-15 + фильтр «глушителя» |
| SG 553 | Winchester Model 1894 |
| AUG | Marlin 336 |
| SSG 08 | Arisaka |
| AWP | Tikka (болтовая винтовка) |
| SCAR-20 | Savage 10 .300 Blackout |
| G3SG1 | Springfield 1917 |
| Zeus x27 | синтезирован нами (щелчок, треск, гул) |

Помповый затвор (`Mech/pump_*`) вырезан из записей Winchester Model 12 этой же библиотеки.
Файлы: `Assets/Resources/Audio/Weapons/`, `Mech/`.

## Шаги, попадания по стенам, механика, упавшее оружие
**Impact Sounds** — Kenney (kenney.nl/assets/impact-sounds), CC0. Файлы: `Assets/Resources/Audio/Footsteps`, `Impacts/wall_*`, `Impacts/knife_*`, `Mech`;
звук упавшего на пол оружия `Audio/Drops/land_*` — из impactMetal_heavy_000–004 и impactMetal_medium_000–004 (`Tools/Audio/make_drop_sounds.py`).

## Попадания по игрокам (freesound.org, CC0)
Слоёные клипы `Assets/Resources/Audio/Impacts/body_*` (попадание в тело) и `head_*` (хэдшот, металлический «тинк»), `Tools/Audio/make_hit_sounds.py`. Использованы HQ-превью с freesound.org, у всех лицензия CC0:
- jawbutch — «Three Flesh Impacts.wav», https://freesound.org/people/jawbutch/sounds/344409/
- u1769092 — «VisceralBulletImpacts.wav», https://freesound.org/people/u1769092/sounds/423301/
- Kinoton — «Power Kick, Body Impact, 7x», https://freesound.org/people/Kinoton/sounds/586841/
- Mafon2 — «Tummy slaps», https://freesound.org/people/Mafon2/sounds/717270/
- JustInvoke — «Bullet Blood 1–4», https://freesound.org/people/JustInvoke/sounds/138478/ (и 138479, 138480, 138481)
- JarredGibb — «Metal Ping 192kHz.wav», https://freesound.org/people/JarredGibb/sounds/219024/
- Empiremonkey — «clank.wav», https://freesound.org/people/Empiremonkey/sounds/616492/
- DDT197 — «Hitting metal .WAV», https://freesound.org/people/DDT197/sounds/445771/
- MadPanCake — «Headshot sound», https://freesound.org/people/MadPanCake/sounds/660769/

## Прыжок и приземление (freesound.org, CC0)
`Assets/Resources/Audio/Movement/jump_*` (шорох одежды + шарканье кроссовок) и `land_*` (подошвы + глухой удар), `Tools/Audio/make_movement_sounds.py`; HQ-превью, лицензия CC0:
- brandondelehoy — «Jacket/Cloth Rustle 9», https://freesound.org/people/brandondelehoy/sounds/494797/
- elynch0901 — «Jumping Human», https://freesound.org/people/elynch0901/sounds/464488/
- SpliceSound — «Footsteps, Tile, Male Sneakers, Jumping.wav», https://freesound.org/people/SpliceSound/sounds/170504/
- SpliceSound — «Footsteps, Tile, Male Tennis Shoes, Scuffs.wav», https://freesound.org/people/SpliceSound/sounds/170499/
- Pelouta — «Footstes sneaker jump.wav», https://freesound.org/people/Pelouta/sounds/689126/
- KieranKeegan — «Landing_Stone_1.wav» / «Landing_Stone_2.wav», https://freesound.org/people/KieranKeegan/sounds/422753/ (и 422754)
- SoundDesignForYou — «Landing On The Ground [2]», https://freesound.org/people/SoundDesignForYou/sounds/646660/
- pcmac — «jump.wav», https://freesound.org/people/pcmac/sounds/464527/

## Перезарядка, затворы (opengameart.org, CC0)
- `reload.wav` — «Handgun Reload Sound Effect» (магазин, затвор пистолета) → пистолеты.
- `assaultriflereload1_0.wav`, `gunreload1.wav` — «Gun reload sounds» (страйкбольные винтовки) → ПП, винтовки, пулемёты.
- `equipment_clicks3.wav` — «equipment clicks III» (затвор болтовой винтовки) → затворы AWP / SSG 08, патроны дробовиков.
- `clipload1.wav`, `clipload2.wav` — «Gun Reload Sound Effects» (Brian MacIntosh) → магазин AWP, патроны дробовиков (`shell_in_*`).
- **RPG Audio** — Kenney (kenney.nl/assets/rpg-audio), CC0 → барабан и перезарядка R8 (`Reload/R8 Revolver/`), щелчки ножа-бабочки (`Knife/flip_*`).
Наборы перезарядки новых стволов — те же записи с другой высотой тона и эквалайзером.
Нарезка и обработка: `Tools/Audio/make_reload_sounds.py`, `make_weapon_sounds.py`. Файлы: `Assets/Resources/Audio/Reload/<оружие>/`.

## Следы ножа, выпавшие магазины и гильзы
Текстура царапин от ножа рисуется кодом (`CombatEffects.cs`), гильзы — примитивы, выпавшие магазины — копии детали `Mag` моделей оружия (Quaternius, см. выше). Сторонних файлов нет.

## Звуки ножа (как в CS 2: воздух / стена / тело)
`Tools/Audio/make_knife_sounds.py`, файлы `Assets/Resources/Audio/Knife/`:
- `air_*` — свист: свой синтез (шум с плавающей полосой) + «Swish - bamboo stick weapon swhoshes» — qubodup (opengameart.org, CC0).
- `wall_*` — чирканье по стене/полу: «Machete vs. Spatula» — CYBERDEViL (opengameart.org, CC0).
- `body_*`, `stab_*` — по телу: «12 wet towel hitting/falling/punching floor sounds» — qubodup (opengameart.org, CC0 с 2024-08-24) + Kenney RPG Audio (knifeSlice2) + Kenney Impact Sounds (impactPunch_heavy).
- `flip_*` — Kenney RPG Audio.

## Иконки килфида
- Силуэты всех 36 видов оружия отрендерены из моделей проекта (`Tools/Icons/make_kill_icons.py`).
- Иконка прострела нарисована нами (`Tools/Icons/make_wallbang_icon.py`).
Файлы: `Assets/Resources/KillIcons/`.

## Звук победы в раунде
`Assets/Resources/Audio/RoundEnd/Default/t_win`, `ct_win` — **Music Jingles**, Kenney (kenney.nl/assets/music-jingles), CC0: jingles_HIT03 и jingles_HIT11 (`Tools/Audio/make_round_end_sounds.py`).
Клипы автора `Audio/RoundEnd/Local/t_win`, `ct_win` (фразы победы T / CT из CS, скачаны автором с lightaudio.ru) играют вместо них; лежат в репозитории по решению автора.

## Текстуры карты
**ambientCG** (ambientcg.com), CC0, 1K-JPG: Bricks084, Plaster007, Ground079S, PavingStones150, Tiles143, WoodFloor043, CorrugatedSteel005, Concrete046.
Файлы: `Assets/Textures/Map/`.
