# Источники ассетов

Все сторонние ассеты — под лицензией **CC0** (общественное достояние): их можно использовать и изменять без ограничений, указание авторства не требуется, но мы его указываем.

## Модели оружия
**Ultimate Gun Pack** — Quaternius (quaternius.com), CC0. Скачано с poly.pizza (зеркало автора).
Доработано в Blender скриптом: реальный масштаб, точки рукояти/ствола/выброса гильз, отделён магазин M4, добавлен глушитель USP-S.

| Оружие в игре | Исходная модель |
|---|---|
| AK-47 | AssaultRifle_2 |
| M4A4 | AssaultRifle2_1 |
| AWP | SniperRifle_1 |
| Glock-18 | Pistol_5 |
| USP-S | Pistol_6 (+ глушитель) |
| Desert Eagle | своя модель (см. ниже) |
| MP9 | SubmachineGun_3 |
| Нож | Bayonet |

Файлы: `Assets/Resources/WeaponModels/`.

## Персонажи
**Ultimate Modular Men Pack** — Quaternius (quaternius.com), CC0, скачано с poly.pizza. Взяты два персонажа со скелетом и 24 анимациями: Swat (CT) → `Assets/Resources/Characters/CT_Swat.fbx`, Adventurer (T) → `T_Adventurer.fbx`.

## Звуки выстрелов
**The Free Firearm Sound Library** — Ben Jaszczak, Brian Nelson, Kevin Heras, Matthew Nanney (freefirearmsfx.com, через opengameart.org), CC0.
Нарезаны одиночные выстрелы, 48 кГц моно, фильтр от низкого гула, нормализация.

| Оружие в игре | Запись |
|---|---|
| AK-47 | AK-47 |
| M4A4 | AR-15 |
| AWP | Tikka (болтовая винтовка) |
| Desert Eagle | Colt 1911 |
| Glock-18 | Walther PPQ |
| MP9 | ППШ |
| USP-S | Ruger Mark III .22 + фильтр «глушителя» |

Файлы: `Assets/Resources/Audio/Weapons/`.

## Шаги, попадания, механика
**Impact Sounds** — Kenney (kenney.nl), CC0. Файлы: `Assets/Resources/Audio/Footsteps`, `Impacts`, `Mech`.

## Перезарядка, затворы, прыжки (opengameart.org, CC0)
- `reload.wav` — «Handgun Reload Sound Effect» (магазин, затвор пистолета) → Glock-18, USP-S, Desert Eagle.
- `assaultriflereload1_0.wav`, `gunreload1.wav` — «Gun reload sounds» (страйкбольные винтовки) → AK-47, M4A4, MP9.
- `equipment_clicks3.wav` — «equipment clicks III» (затвор болтовой винтовки) → затвор AWP; `equipmentclicks.wav` — «Equipment Clicks II» → щелчок ножа.
- `clipload1.wav`, `clipload2.wav` — «Gun Reload Sound Effects» (Brian MacIntosh) → магазин AWP.
- `Jump 1-3.wav` — «Jump Landing» (Dan Knoflicek), `jumpland.wav` — «Jump Landing Sound» → прыжок и приземление.
Нарезка и обработка: `Tools/Audio/make_reload_sounds.py`, `Tools/Audio/make_movement_sounds.py`. Файлы: `Assets/Resources/Audio/Reload/<оружие>/`, `Movement/`, `Knife/flip_*`.

## Desert Eagle — своя модель
Сделана с нуля скриптом Blender `Tools/Blender/make_deagle.py` (без сторонних ассетов): затвор (`Slide`) и магазин (`Mag`) — отдельные детали.

## Иконки килфида
- Силуэты оружия отрендерены из моделей проекта (`Tools/Icons/make_kill_icons.py`).
- Иконка прострела нарисована нами (`Tools/Icons/make_wallbang_icon.py`).
Файлы: `Assets/Resources/KillIcons/`.

## Свист ножа
Синтезирован в Python (полосовой шум), свой. `Assets/Resources/Audio/Knife/`.

## Текстуры карты
**ambientCG** (ambientcg.com), CC0, 1K-JPG: Bricks084, Plaster007, Ground079S, PavingStones150, Tiles143, WoodFloor043, CorrugatedSteel005, Concrete046.
Файлы: `Assets/Textures/Map/`.
