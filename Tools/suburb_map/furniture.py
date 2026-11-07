"""Voxel furniture and per-room placement recipes.

Items are built in a wall-relative frame: u runs along the wall, w is the distance out from the
wall, z is height above the floor. Each room type has a recipe that arranges items the way a
real room is laid out (sofa facing the TV, nightstands beside the bed, one continuous kitchen run,
tables in the middle). Placement always keeps doorways, stairs and a walking path clear, and
never leaves a gap narrower than the player next to free-standing furniture.
"""
from collections import deque

# Item = (width along wall, depth from wall, height, boxes[, props]);
# box = (u0, w0, z0, u1, w1, z1, colour[, group]). `props` maps a box group to a physics-prop kind:
# those boxes become a separate movable object instead of fixed voxels.

# Physics prop kinds: name -> (label shown to players, mass kg, health). The order is the kind id
# in the map file, which also stores the labels, so the game never hard-codes this list.
# One player lifts up to 40 kg (above 15 kg it pulls with visible effort); 40-120 kg needs two players.
PROP_KINDS = {
    'chair': ('Стул', 6, 40), 'stool': ('Табурет', 4, 30), 'crate': ('Ящик', 15, 60),
    'plant': ('Растение', 8, 30), 'big_plant': ('Большое растение', 14, 40),
    'toybox': ('Ящик с игрушками', 8, 40), 'basket': ('Корзина', 3, 20), 'armchair': ('Кресло', 30, 80),
    'coffee_table': ('Журнальный столик', 14, 60), 'table': ('Стол', 30, 90), 'cafe_table': ('Столик', 16, 70),
    'nightstand': ('Тумбочка', 12, 50), 'lamp': ('Лампа', 5, 20), 'bin': ('Мусорный бак', 12, 60),
    'cone': ('Конус', 2, 30), 'pallet': ('Поддон', 15, 60), 'barrel': ('Бочка', 30, 120), 'box': ('Коробка', 6, 25),
    'monitor': ('Монитор', 5, 25), 'desk': ('Письменный стол', 35, 90), 'tv': ('Телевизор', 15, 30),
    'tv_stand': ('Тумба', 20, 70), 'dresser': ('Комод', 45, 110), 'cabinet': ('Шкафчик', 35, 90),
    'bookshelf': ('Книжный шкаф', 60, 130), 'wardrobe': ('Шкаф', 85, 160), 'bed': ('Кровать', 70, 150),
    'double_bed': ('Двуспальная кровать', 95, 180), 'bunk_bed': ('Двухъярусная кровать', 110, 180),
    'sofa': ('Диван', 65, 140), 'fridge': ('Холодильник', 90, 200), 'washer': ('Стиральная машина', 60, 150),
    'dryer': ('Сушильная машина', 50, 140), 'locker': ('Шкафчик', 50, 140), 'rack': ('Стеллаж', 55, 130),
    'arcade': ('Игровой автомат', 90, 150), 'treadmill': ('Беговая дорожка', 100, 160),
    'weights': ('Стойка с гантелями', 60, 200), 'bench': ('Скамья', 35, 100), 'coat_rack': ('Вешалка', 6, 30),
    'shoe_rack': ('Обувница', 10, 40),
    'plank_s': ('Короткая доска', 3, 35), 'plank_m': ('Доска', 5, 45), 'plank_l': ('Длинная доска', 8, 55),
    'radio': ('Радио', 3, 25),
}
PROP_IDS = {name: i for i, name in enumerate(PROP_KINDS)}
# Items that become physics props: box group -> kind. Ungrouped boxes are group 'main'.
# Built-ins (kitchen counters, stove, bath, toilet, piano, fireplace...) stay part of the building.
ITEM_PROPS = {
    'nightstand': {'main': 'nightstand'}, 'armchair': {'main': 'armchair'}, 'floor_lamp': {'main': 'lamp'},
    'basket': {'main': 'basket'}, 'plant': {'main': 'plant'}, 'big_plant': {'main': 'big_plant'},
    'crate': {'main': 'crate'}, 'toybox': {'main': 'toybox'}, 'crates': {'a': 'crate', 'b': 'crate'},
    'wardrobe': {'main': 'wardrobe'}, 'dresser': {'main': 'dresser', 'p': 'plant'},
    'bookshelf': {'main': 'bookshelf'}, 'tv': {'main': 'tv_stand', 's': 'tv'},
    'desk': {'main': 'desk', 'm': 'monitor', 'c': 'chair'}, 'fridge': {'main': 'fridge'},
    'washer': {'main': 'washer'}, 'dryer': {'main': 'dryer'}, 'locker': {'main': 'locker'},
    'cabinet': {'main': 'cabinet', 'l': 'lamp'}, 'rack': {'main': 'rack', 'a': 'crate', 'b': 'crate', 'c': 'crate'},
    'bunk_bed': {'main': 'bunk_bed'}, 'arcade': {'main': 'arcade'}, 'treadmill': {'main': 'treadmill'},
    'weights': {'main': 'weights'}, 'coat_rack': {'main': 'coat_rack'}, 'shoe_rack': {'main': 'shoe_rack'},
    'planks': {'a': 'plank_l', 'b': 'plank_m', 'c': 'plank_s', 'd': 'plank_m', 'e': 'plank_s'},
    'radio': {'main': 'radio'},
}


def _bed(c, width=10):
    """A bed on legs: 0.5 m of clear space underneath, enough to crawl under it lying down."""
    return (width, 15, 8, [
        (0, 0, 0, 1, 1, 3, 'wood_dark'), (width - 1, 0, 0, width, 1, 3, 'wood_dark'),
        (0, 14, 0, 1, 15, 3, 'wood_dark'), (width - 1, 14, 0, width, 15, 3, 'wood_dark'),
        (0, 0, 3, width, 15, 4, 'wood'), (0, 1, 4, width, 15, 5, 'mattress'), (0, 6, 5, width, 15, 6, c),
        (1, 1, 5, width - 1, 4, 6, 'pillow'), (0, 0, 4, width, 1, 9, 'wood_dark')])


def _sofa(c, width=14):
    return (width, 6, 5, [
        (0, 0, 0, width, 6, 2, c), (0, 0, 2, width, 2, 5, c), (0, 2, 2, 1, 6, 4, c),
        (width - 1, 2, 2, width, 6, 4, c), (1, 2, 2, width - 1, 6, 3, 'cushion')])


ITEMS = {
    'nightstand': (3, 3, 6, [(0, 0, 0, 3, 3, 4, 'wood'), (1, 1, 4, 2, 2, 6, 'lamp')]),
    'wardrobe': (9, 4, 13, [(0, 0, 0, 9, 4, 13, 'wood'), (4, 3, 1, 5, 4, 12, 'wood_dark'),
                            (3, 3, 6, 4, 4, 7, 'metal'), (5, 3, 6, 6, 4, 7, 'metal')]),
    'dresser': (8, 4, 8, [(0, 0, 0, 8, 4, 6, 'wood'), (1, 3, 1, 7, 4, 2, 'wood_dark'),
                          (1, 3, 3, 7, 4, 4, 'wood_dark'), (2, 1, 6, 4, 3, 8, 'plant', 'p')]),
    'bookshelf': (8, 3, 12, [(0, 0, 0, 8, 3, 12, 'wood'), (1, 2, 1, 3, 3, 3, 'book1'), (3, 2, 1, 6, 3, 3, 'book2'),
                             (1, 2, 4, 4, 3, 6, 'book3'), (5, 2, 4, 7, 3, 6, 'book4'), (2, 2, 7, 7, 3, 9, 'book1'),
                             (1, 2, 10, 5, 3, 11, 'book2')]),
    'tv': (10, 3, 8, [(0, 0, 0, 10, 3, 3, 'wood_dark'), (1, 0, 3, 9, 1, 8, 'screen', 's')]),
    'desk': (10, 8, 8, [(0, 0, 4, 10, 5, 5, 'wood'), (0, 0, 0, 1, 5, 4, 'wood'), (9, 0, 0, 10, 5, 4, 'wood'),
                        (3, 0, 5, 7, 1, 8, 'screen', 'm'), (4, 6, 0, 7, 8, 3, 'fabric3', 'c'),
                        (4, 7, 3, 7, 8, 6, 'fabric3', 'c')]),
    'armchair': (6, 6, 6, [(0, 0, 0, 6, 6, 3, 'fabric4'), (0, 0, 3, 6, 2, 6, 'fabric4'), (0, 2, 3, 1, 6, 4, 'fabric4'),
                           (5, 2, 3, 6, 6, 4, 'fabric4')]),
    'floor_lamp': (2, 2, 11, [(0, 0, 0, 2, 2, 1, 'metal'), (0, 0, 1, 1, 1, 9, 'metal'), (0, 0, 9, 2, 2, 11, 'lamp')]),
    'fridge': (5, 5, 12, [(0, 0, 0, 5, 5, 12, 'fridge'), (0, 4, 7, 5, 5, 8, 'metal')]),
    'stove': (5, 5, 12, [(0, 0, 0, 5, 5, 6, 'black'), (1, 1, 6, 4, 4, 6, 'metal'), (0, 0, 10, 5, 3, 12, 'metal')]),
    'counter': (8, 5, 6, [(0, 0, 0, 8, 5, 5, 'white'), (0, 0, 5, 8, 5, 6, 'counter_top')]),
    'counter_tall': (8, 5, 12, [(0, 0, 0, 8, 5, 5, 'white'), (0, 0, 5, 8, 5, 6, 'counter_top'), (0, 0, 9, 8, 3, 12, 'wood')]),
    'sink_counter': (6, 5, 6, [(0, 0, 0, 6, 5, 5, 'white'), (0, 0, 5, 6, 5, 6, 'counter_top'),
                               (2, 1, 5, 4, 3, 6, 'metal')]),
    'counter_short': (5, 5, 6, [(0, 0, 0, 5, 5, 5, 'white'), (0, 0, 5, 5, 5, 6, 'counter_top')]),
    'bathtub': (7, 13, 4, [(0, 0, 0, 7, 13, 4, 'white'), (1, 1, 1, 6, 12, 2, 'water')]),
    'toilet': (4, 5, 6, [(1, 1, 0, 3, 5, 3, 'white'), (0, 0, 0, 4, 1, 6, 'white')]),
    'sink': (5, 4, 10, [(0, 0, 0, 5, 4, 5, 'white'), (1, 1, 5, 4, 3, 6, 'metal'), (1, 0, 7, 4, 1, 10, 'mirror')]),
    'shower': (7, 7, 13, [(0, 0, 0, 7, 7, 1, 'floor_tile'), (6, 0, 1, 7, 7, 13, 'glass_block'),
                          (0, 6, 1, 4, 7, 13, 'glass_block'), (3, 0, 10, 4, 1, 12, 'metal')]),
    'washer': (5, 5, 6, [(0, 0, 0, 5, 5, 6, 'white'), (1, 4, 2, 4, 5, 5, 'black')]),
    'dryer': (5, 5, 6, [(0, 0, 0, 5, 5, 6, 'white'), (1, 4, 2, 4, 5, 5, 'metal')]),
    'basket': (3, 3, 3, [(0, 0, 0, 3, 3, 3, 'crate')]),
    'plant': (3, 3, 8, [(0, 0, 0, 3, 3, 3, 'pot'), (0, 0, 3, 3, 3, 7, 'plant'), (1, 1, 7, 2, 2, 8, 'plant')]),
    'big_plant': (4, 4, 11, [(1, 1, 0, 3, 3, 3, 'pot'), (0, 0, 3, 4, 4, 9, 'plant'), (1, 1, 9, 3, 3, 11, 'plant')]),
    'crates': (5, 5, 10, [(0, 0, 0, 5, 5, 5, 'crate', 'a'), (1, 1, 5, 5, 5, 10, 'crate_dark', 'b')]),
    'crate': (5, 5, 5, [(0, 0, 0, 5, 5, 5, 'crate')]),
    'rack': (12, 4, 12, [(0, 0, 0, 1, 4, 12, 'metal'), (11, 0, 0, 12, 4, 12, 'metal'), (0, 0, 4, 12, 4, 5, 'metal'),
                         (0, 0, 8, 12, 4, 9, 'metal'), (0, 0, 11, 12, 4, 12, 'metal'), (1, 0, 0, 5, 4, 3, 'crate', 'a'),
                         (6, 0, 5, 11, 4, 8, 'crate_dark', 'b'), (2, 0, 9, 7, 4, 11, 'crate', 'c')]),
    'locker': (6, 4, 12, [(0, 0, 0, 6, 4, 12, 'metal'), (3, 3, 1, 4, 4, 11, 'black')]),
    'cabinet': (10, 3, 9, [(0, 0, 0, 10, 3, 7, 'wood'), (5, 2, 1, 6, 3, 6, 'wood_dark'), (2, 1, 7, 4, 2, 9, 'lamp', 'l')]),
    'mailboxes': (10, 2, 10, [(0, 0, 3, 10, 2, 10, 'metal'), (1, 1, 4, 9, 2, 9, 'black')]),
    'booth': (14, 6, 8, [(0, 0, 0, 3, 6, 3, 'seat'), (0, 0, 3, 1, 6, 8, 'seat'), (11, 0, 0, 14, 6, 3, 'seat'),
                         (13, 0, 3, 14, 6, 8, 'seat'), (5, 0, 4, 9, 6, 5, 'counter_top'), (6, 2, 0, 8, 4, 4, 'metal')]),
    'bunk_bed': (10, 15, 13, [(0, 0, 0, 1, 1, 13, 'wood'), (9, 0, 0, 10, 1, 13, 'wood'), (0, 14, 0, 1, 15, 13, 'wood'),
                              (9, 14, 0, 10, 15, 13, 'wood'), (0, 0, 1, 10, 15, 2, 'wood'), (1, 1, 2, 9, 14, 3, 'mattress'),
                              (0, 0, 7, 10, 15, 8, 'wood'), (1, 1, 8, 9, 14, 9, 'fabric2'), (9, 6, 2, 10, 7, 9, 'wood'),
                              (9, 9, 2, 10, 10, 9, 'wood')]),
    'toybox': (5, 3, 4, [(0, 0, 0, 5, 3, 3, 'fabric1'), (0, 0, 3, 5, 3, 4, 'book3')]),
    'arcade': (5, 5, 13, [(0, 0, 0, 5, 5, 13, 'black'), (1, 4, 7, 4, 5, 11, 'screen_on'), (1, 4, 5, 4, 5, 6, 'seat')]),
    'treadmill': (5, 12, 8, [(0, 0, 0, 5, 12, 1, 'black'), (0, 0, 1, 1, 2, 7, 'metal'), (4, 0, 1, 5, 2, 7, 'metal'),
                             (0, 0, 7, 5, 2, 8, 'metal')]),
    'weights': (8, 3, 6, [(0, 0, 0, 8, 3, 1, 'metal'), (0, 0, 1, 1, 3, 6, 'metal'), (7, 0, 1, 8, 3, 6, 'metal'),
                          (1, 1, 2, 7, 2, 3, 'black'), (1, 1, 4, 7, 2, 5, 'black')]),
    'piano': (10, 6, 9, [(0, 0, 0, 10, 4, 9, 'black'), (0, 3, 5, 10, 4, 6, 'white'), (3, 5, 0, 7, 6, 3, 'black')]),
    'fireplace': (10, 4, 12, [(0, 0, 0, 10, 3, 11, 'brick'), (3, 2, 0, 7, 3, 5, 'black'), (0, 0, 11, 10, 4, 12, 'wood_dark')]),
    'coat_rack': (3, 2, 11, [(1, 0, 0, 2, 1, 11, 'wood_dark'), (0, 0, 9, 3, 2, 11, 'fabric2')]),
    'shoe_rack': (6, 3, 4, [(0, 0, 0, 6, 3, 4, 'wood')]),
    'workbench': (12, 5, 8, [(0, 0, 4, 12, 5, 5, 'wood'), (0, 0, 0, 1, 5, 4, 'wood_dark'),
                             (11, 0, 0, 12, 5, 4, 'wood_dark'), (1, 0, 5, 11, 1, 8, 'metal')]),
    # Built-in wardrobe a player can climb into: a 1 m x 0.8 m x 2.1 m space behind two leaves that open
    # outwards. Doors are (u0, u1, w, height, hinge at u1): leaves on the line w = 6, recessed one voxel.
    'hide_closet': (8, 7, 14, [(0, 0, 0, 8, 1, 14, 'wood'), (0, 1, 0, 1, 7, 14, 'wood'), (7, 1, 0, 8, 7, 14, 'wood'),
                               (1, 1, 13, 7, 7, 14, 'wood')], None, [(1, 4, 6, 13, 0), (4, 7, 6, 13, 1)]),
    # Boards for barricades, stacked on two bearers: every board is its own prop.
    'planks': (16, 3, 6, [(1, 0, 0, 2, 2, 1, 'wood_dark'), (13, 0, 0, 14, 2, 1, 'wood_dark'),
                          (0, 0, 1, 16, 2, 2, 'pallet', 'a'), (2, 0, 2, 14, 2, 3, 'pallet', 'b'),
                          (4, 0, 3, 12, 2, 4, 'pallet', 'c'), (2, 0, 4, 14, 2, 5, 'pallet', 'd'),
                          (4, 0, 5, 12, 2, 6, 'pallet', 'e')]),
    'radio': (4, 2, 3, [(0, 0, 0, 4, 2, 3, 'black'), (1, 1, 1, 3, 2, 2, 'metal')]),
    'shop_counter': (14, 5, 8, [(0, 0, 0, 14, 5, 5, 'shop_counter'), (0, 0, 5, 14, 5, 6, 'counter_top'),
                                (3, 1, 6, 6, 3, 8, 'screen')]),
}


def item(name, colour='fabric1'):
    if name == 'bed':
        return _bed(colour) + ({'main': 'bed'},)
    if name == 'double_bed':
        return _bed(colour, 14) + ({'main': 'double_bed'},)
    if name == 'sofa':
        return _sofa(colour) + ({'main': 'sofa'},)
    spec = ITEMS[name]
    if name in ITEM_PROPS:
        return spec[:4] + (ITEM_PROPS[name],) + spec[5:]
    return spec


# Clear floor kept in front of a hiding wardrobe: its leaves swing out and a player must reach it.
FRONT_CLEAR = 5


# Free-standing pieces, laid out on their own grid (u = x, w = y). Tables and chairs are props.
def coffee_table():
    return (10, 6, 3, [(0, 0, 2, 10, 6, 3, 'wood'), (0, 0, 0, 1, 1, 2, 'wood'), (9, 0, 0, 10, 1, 2, 'wood'),
                       (0, 5, 0, 1, 6, 2, 'wood'), (9, 5, 0, 10, 6, 2, 'wood')], {'main': 'coffee_table'})


def _chair(boxes, props, x, y, facing, colour, n):
    """A chair with its back on the far side from the table. facing: +1 table is +y, -1 table is -y,
    +2 table is +x, -2 table is -x."""
    g = 'c%d' % n
    props[g] = 'chair'
    boxes.append((x, y, 0, x + 3, y + 3, 3, colour, g))
    if facing == 1:
        boxes.append((x, y, 3, x + 3, y + 1, 7, colour, g))
    elif facing == -1:
        boxes.append((x, y + 2, 3, x + 3, y + 3, 7, colour, g))
    elif facing == 2:
        boxes.append((x, y, 3, x + 1, y + 3, 7, colour, g))
    else:
        boxes.append((x + 2, y, 3, x + 3, y + 3, 7, colour, g))


def dining_set(chair='fabric4', length=10):
    """A table (1 m deep) with chairs along both sides and at the ends; every piece is a prop."""
    w = length + 8
    t = 'table'
    boxes = [(4, 3, 4, 4 + length, 9, 5, 'wood', t), (4, 3, 0, 5, 4, 4, 'wood', t),
             (3 + length, 3, 0, 4 + length, 4, 4, 'wood', t), (4, 8, 0, 5, 9, 4, 'wood', t),
             (3 + length, 8, 0, 4 + length, 9, 4, 'wood', t)]
    props = {t: 'table'}
    n = 0
    for cx in range(6, 4 + length - 3, 5):
        _chair(boxes, props, cx, 0, 1, chair, n)
        _chair(boxes, props, cx, 9, -1, chair, n + 1)
        n += 2
    _chair(boxes, props, 0, 4, 2, chair, n)
    _chair(boxes, props, w - 3, 4, -2, chair, n + 1)
    return (w, 12, 7, boxes, props)


def small_dining(chair='fabric4'):
    return dining_set(chair, length=7)


def kitchen_island():
    return (12, 8, 6, [(0, 0, 0, 12, 6, 5, 'white'), (0, 0, 5, 12, 6, 6, 'counter_top'),
                       (2, 6, 0, 4, 8, 4, 'metal'), (8, 6, 0, 10, 8, 4, 'metal')])


def pool_table():
    return (11, 18, 5, [(0, 0, 3, 11, 18, 5, 'wood_dark'), (1, 1, 4, 10, 17, 5, 'felt'), (1, 1, 0, 3, 3, 3, 'wood_dark'),
                        (8, 1, 0, 10, 3, 3, 'wood_dark'), (1, 15, 0, 3, 17, 3, 'wood_dark'), (8, 15, 0, 10, 17, 3, 'wood_dark')])


def bench_press():
    return (5, 11, 8, [(1, 0, 0, 4, 11, 3, 'black'), (0, 8, 0, 1, 11, 8, 'metal'), (4, 8, 0, 5, 11, 8, 'metal'),
                       (0, 9, 7, 5, 10, 8, 'metal')], {'main': 'bench'})


def cafe_table():
    """A 1 m square cafe table with four stools; all props."""
    props = {'t': 'cafe_table', 's0': 'stool', 's1': 'stool', 's2': 'stool', 's3': 'stool'}
    return (12, 12, 7, [(3, 3, 4, 9, 9, 5, 'counter_top', 't'), (5, 5, 0, 7, 7, 4, 'metal', 't'),
                        (0, 4, 0, 3, 8, 3, 'seat', 's0'), (9, 4, 0, 12, 8, 3, 'seat', 's1'),
                        (4, 0, 0, 8, 3, 3, 'seat', 's2'), (4, 9, 0, 8, 12, 3, 'seat', 's3')], props)


FABRICS = ('fabric1', 'fabric2', 'fabric3', 'fabric4')
RUGS = ('rug1', 'rug2', 'rug3')
OPPOSITE = {'S': 'N', 'N': 'S', 'W': 'E', 'E': 'W'}
CLEARANCE = 3   # free cells kept around free-standing furniture (0.5 m): the player never gets wedged


class Furnisher:
    """Places furniture inside one room of a canvas at floor height `zf`."""

    def __init__(self, canvas, rect, zf, keep_clear, low_only, rng, colours):
        self.cv, self.zf, self.rng, self.col = canvas, zf, rng, colours
        self.x0, self.y0, self.x1, self.y1 = rect
        self.keep = keep_clear      # cells that must stay walkable (doors, stairs)
        self.low = low_only         # cells in front of windows: only furniture below the sill
        self.used = set()
        self.clear = set()          # walkable, but nothing may be placed there (in front of wardrobes)
        self.base = None

    @property
    def width(self):
        return self.x1 - self.x0

    @property
    def depth(self):
        return self.y1 - self.y0

    def inside(self, x, y):
        return self.x0 <= x < self.x1 and self.y0 <= y < self.y1

    # ---- connectivity: furniture may never cut a path between doors and stairs
    def walkable(self):
        free = {(x, y) for x in range(self.x0, self.x1) for y in range(self.y0, self.y1) if (x, y) not in self.used}
        return {(x, y) for (x, y) in free
                if all((x + i, y + j) in free for i in range(-1, 3) for j in range(-1, 3))}

    def connectivity(self):
        targets = [c for c in self.keep if self.inside(*c)]
        if not targets:
            return 0
        walk = self.walkable()
        zones = {}
        for c in targets:
            if c in walk:
                zones.setdefault(self.keep[c], []).append(c)
        best, seen_all = 0, set()
        for cells in zones.values():
            start = cells[0]
            if start in seen_all:
                continue
            seen = {start}
            queue = deque([start])
            while queue:
                x, y = queue.popleft()
                for n in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
                    if n in walk and n not in seen:
                        seen.add(n)
                        queue.append(n)
            seen_all |= seen
            best = max(best, sum(1 for z in zones.values() if any(c in seen for c in z)))
        return best

    def _rect_ok(self, x0, y0, x1, y1, height):
        if x0 < self.x0 or y0 < self.y0 or x1 > self.x1 or y1 > self.y1:
            return False
        for x in range(x0, x1):
            for y in range(y0, y1):
                if (x, y) in self.used or (x, y) in self.keep or (x, y) in self.clear:
                    return False
                if height > 6 and (x, y) in self.low:  # counters may sit just under a sill
                    return False
        return True

    def _commit(self, cells, draw):
        if self.base is None:
            self.base = self.connectivity()
        before = set(self.used)
        self.used.update(cells)
        if self.connectivity() < self.base:
            self.used = before
            return False
        draw()
        return True

    # ---- wall placement
    def _wall_rect(self, side, p, width, depth):
        if side == 'S':
            return self.x0 + p, self.y0, self.x0 + p + width, self.y0 + depth
        if side == 'N':
            return self.x0 + p, self.y1 - depth, self.x0 + p + width, self.y1
        if side == 'W':
            return self.x0, self.y0 + p, self.x0 + depth, self.y0 + p + width
        return self.x1 - depth, self.y0 + p, self.x1, self.y0 + p + width

    def span(self, side):
        return self.width if side in 'SN' else self.depth

    def try_at(self, spec, side, p):
        width, depth, height, boxes = spec[:4]
        props = spec[4] if len(spec) > 4 else None
        doors = spec[5] if len(spec) > 5 else None
        if p < 0 or p + width > self.span(side):
            return False
        rect = self._wall_rect(side, p, width, depth)
        if not self._rect_ok(*rect, height):
            return False
        front = set()
        if doors:
            # The leaves need room to swing and the player room to step in: that floor must be free.
            reach = self._wall_rect(side, p, width, depth + FRONT_CLEAR)
            front = {(x, y) for x in range(reach[0], reach[2]) for y in range(reach[1], reach[3])
                     if not (rect[0] <= x < rect[2] and rect[1] <= y < rect[3])}
            if any(not self.inside(*c) or c in self.used or c in self.clear for c in front):
                return False
        cells = {(x, y) for x in range(rect[0], rect[2]) for y in range(rect[1], rect[3])}
        if not self._commit(cells, lambda: self._draw(boxes, side, p, width, props=props, doors=doors)):
            return False
        self.clear |= front
        return True

    def place_wall(self, spec, sides=None, centre=False, corner=False):
        """Back against a wall. Returns (side, p) or None."""
        width = spec[0]
        sides = list(sides or 'SNWE')
        self.rng.shuffle(sides)
        options = []
        for side in sides:
            n = self.span(side) - width
            if n < 0:
                continue
            ps = list(range(n + 1))
            if corner:
                ps = [0, n]
            elif centre:
                ps.sort(key=lambda p: abs(p - n / 2) + self.rng.random() * 2)
            else:
                self.rng.shuffle(ps)
            options.append((side, ps))
        for side, ps in options:
            for p in ps[:40]:
                if self.try_at(spec, side, p):
                    return side, p
        return None

    # ---- free-standing placement with walk-around clearance
    def place_centre(self, spec, jitter=3):
        width, depth = spec[0], spec[1]
        if self.width < width + 2 * CLEARANCE or self.depth < depth + 2 * CLEARANCE:
            return False
        cx = self.x0 + (self.width - width) // 2
        cy = self.y0 + (self.depth - depth) // 2
        cands = [(cx + dx, cy + dy) for dx in range(-jitter, jitter + 1) for dy in range(-jitter, jitter + 1)]
        cands.sort(key=lambda c: abs(c[0] - cx) + abs(c[1] - cy) + self.rng.random())
        return self._place_free_at(spec, cands)

    def place_rows(self, spec, gap=4):
        """Fill the open middle of a big room with copies of a free-standing piece, in rows."""
        width, depth = spec[0], spec[1]
        placed = 0
        for y in range(self.y0 + CLEARANCE, self.y1 - depth - CLEARANCE + 1, depth + gap):
            for x in range(self.x0 + CLEARANCE, self.x1 - width - CLEARANCE + 1, width + gap):
                if self._place_free_at(spec, [(x + dx, y + dy) for dx in (0, 1, -1, 2) for dy in (0, 1, -1)]):
                    placed += 1
        return placed

    def _place_free_at(self, spec, cands):
        width, depth, height, boxes = spec[:4]
        props = spec[4] if len(spec) > 4 else None
        for x, y in cands:
            if x - CLEARANCE < self.x0 or y - CLEARANCE < self.y0 or \
                    x + width + CLEARANCE > self.x1 or y + depth + CLEARANCE > self.y1:
                continue
            ring_ok = all((i, j) not in self.used
                          for i in range(x - CLEARANCE, x + width + CLEARANCE)
                          for j in range(y - CLEARANCE, y + depth + CLEARANCE))
            if not ring_ok or not self._rect_ok(x, y, x + width, y + depth, height):
                continue
            cells = {(i, j) for i in range(x, x + width) for j in range(y, y + depth)}
            if self._commit(cells, lambda a=x, b=y: self._draw(boxes, 'S', a - self.x0, width, b - self.y0, props)):
                return True
        return False

    def rug(self):
        w = min(16, self.width - 6)
        d = min(11, self.depth - 6)
        if w < 6 or d < 5:
            return
        x = self.x0 + (self.width - w) // 2
        y = self.y0 + (self.depth - d) // 2
        c = self.col[self.rng.choice(RUGS)]
        for i in range(x, x + w):
            for j in range(y, y + d):
                if (i, j, self.zf - 1) in self.cv.vox:
                    self.cv.vox[(i, j, self.zf - 1)] = c

    def _draw(self, boxes, side, p, width, offset_w=0, props=None, doors=None):
        loose = {}
        # Below the ground (basements) fixed furniture is static terrain, like the cellar itself.
        fixed = self.cv.under if self.zf < 1 else self.cv.vox
        for box in boxes:
            u0, w0, z0, u1, w1, z1, colour = box[:7]
            group = box[7] if len(box) > 7 else 'main'
            c = self.col[colour]
            target = fixed
            if props and group in props:
                target = loose.setdefault(group, {})
            for u in range(u0, u1):
                for w in range(w0, w1):
                    ww = w + offset_w
                    if side == 'S':
                        x, y = self.x0 + p + u, self.y0 + ww
                    elif side == 'N':
                        x, y = self.x0 + p + width - 1 - u, self.y1 - 1 - ww
                    elif side == 'W':
                        x, y = self.x0 + ww, self.y0 + p + width - 1 - u
                    else:
                        x, y = self.x1 - 1 - ww, self.y0 + p + u
                    for z in range(z0, z1):
                        target[(x, y, self.zf + z)] = c
        for group, vox in loose.items():
            self.cv.props.append((props[group], vox))
        for u0, u1, line, height, hinge in doors or ():
            self._door(side, p, width, u0, u1, line, height, hinge)

    def _door(self, side, p, width, u0, u1, line, height, hinge):
        """A leaf on the item's line w = `line`, spanning u0..u1, always opening away from the wall."""
        w = u1 - u0
        if side == 'S':
            door = (self.x0 + p + u0, self.y0 + line, 1, hinge, (0, 1))
        elif side == 'N':
            door = (self.x0 + p + width - u1, self.y1 - line, 1, 1 - hinge, (0, -1))
        elif side == 'W':
            door = (self.x0 + line, self.y0 + p + width - u1, 0, 1 - hinge, (1, 0))
        else:
            door = (self.x1 - line, self.y0 + p + u0, 0, hinge, (-1, 0))
        x, y, plane, h, outward = door
        self.cv.doors.append((x, y, self.zf, w, height, plane, h, outward, self.col['wood']))

    # ---- recipes
    def kitchen_run(self, pieces):
        """Counters, sink, stove and fridge as one continuous run along the longest usable wall."""
        sides = sorted('SNWE', key=lambda s: -self.span(s))
        for side in sides:
            n = self.span(side)
            for start in (0, 1, 2, 3, 6, 9):
                p, placed = start, 0
                for name in pieces:
                    spec = item(name)
                    if p + spec[0] > n:
                        continue
                    if self.try_at(spec, side, p):
                        p += spec[0]
                        placed += 1
                if placed >= 2:
                    return side
        return None

    def bed_set(self, bed):
        spot = self.place_wall(bed, centre=True)
        if not spot:
            return
        side, p = spot
        stand = item('nightstand')
        self.try_at(stand, side, p - stand[0])
        self.try_at(stand, side, p + bed[0])

    def corners(self, name, count=2):
        for _ in range(count):
            self.place_wall(item(name), corner=True)

    def furnish(self, room_type):
        recipe = getattr(self, 'room_' + room_type, None)
        if recipe:
            recipe(self.rng.choice(FABRICS), self.rng)

    def room_living(self, fabric, r):
        if r.random() < 0.35:
            self.place_wall(item('hide_closet'))
        spot = self.place_wall(item('sofa', fabric), centre=True)
        if spot:
            self.place_wall(item('tv'), sides=[OPPOSITE[spot[0]]], centre=True)
        self.rug()
        self.place_centre(coffee_table())
        self.place_wall(item('armchair'))
        self.place_wall(item('fireplace' if r.random() < 0.4 else 'bookshelf'))
        self.place_wall(item('floor_lamp'), corner=True)
        self.corners('big_plant', 1)
        self.corners('plant', 1)

    def room_kitchen(self, fabric, r):
        self.kitchen_run(['fridge', 'counter_tall', 'counter', 'sink_counter', 'counter_short', 'stove', 'counter',
                          'counter_short'])
        if not (self.place_centre(dining_set(length=9)) or self.place_centre(small_dining()) or
                self.place_centre(kitchen_island())):
            self.place_wall(small_dining())
        self.place_wall(item('radio'))
        self.corners('plant', 1)

    def room_dining(self, fabric, r):
        if not (self.place_centre(dining_set(length=14)) or self.place_centre(dining_set(length=9)) or
                self.place_centre(small_dining())):
            self.place_wall(small_dining())
        self.place_wall(item('cabinet'), centre=True)
        self.corners('big_plant', 2)

    def room_bedroom(self, fabric, r):
        self.bed_set(item('double_bed' if self.width >= 18 and self.depth >= 18 else 'bed', fabric))
        if not self.place_wall(item('hide_closet')):
            self.place_wall(item('wardrobe'))
        self.place_wall(item('dresser'))
        self.rug()
        self.corners('plant', 1)

    def room_kids(self, fabric, r):
        self.place_wall(item('bunk_bed'), corner=True)
        self.place_wall(item('hide_closet'))
        self.place_wall(item('desk'))
        self.place_wall(item('toybox'))
        self.place_wall(item('toybox'))
        self.place_wall(item('bookshelf'))
        self.rug()

    def room_bathroom(self, fabric, r):
        if not self.place_wall(item('bathtub'), corner=True):
            self.place_wall(item('shower'), corner=True)
        self.place_wall(item('toilet'))
        self.place_wall(item('sink'))

    def room_study(self, fabric, r):
        self.place_wall(item('desk'), centre=True)
        self.place_wall(item('bookshelf'))
        self.place_wall(item('bookshelf'))
        self.place_wall(item('armchair'))
        self.place_wall(item('floor_lamp'), corner=True)
        self.rug()

    def room_library(self, fabric, r):
        for _ in range(4):
            self.place_wall(item('bookshelf'))
        self.place_wall(item('armchair'))
        self.place_wall(item('armchair'))
        self.place_wall(item('piano'))
        self.rug()

    def room_games(self, fabric, r):
        self.place_centre(pool_table())
        self.place_wall(item('arcade'), corner=True)
        self.place_wall(item('arcade'), corner=True)
        self.place_wall(item('sofa', fabric))
        self.place_wall(item('tv'))

    def room_gym(self, fabric, r):
        self.place_wall(item('treadmill'))
        self.place_wall(bench_press())
        self.place_wall(item('weights'))
        self.place_wall(item('locker'))
        self.corners('plant', 1)

    def room_laundry(self, fabric, r):
        self.kitchen_run(['washer', 'dryer', 'counter_short', 'washer'])
        self.place_wall(item('rack'))
        self.place_wall(item('basket'))
        self.place_wall(item('basket'))

    def room_closet(self, fabric, r):
        self.place_wall(item('hide_closet'))
        self.place_wall(item('wardrobe'))
        self.place_wall(item('rack'))
        self.place_wall(item('crates'))
        self.place_wall(item('crates'))

    def room_hall(self, fabric, r):
        # Most hallways have a built-in coat cupboard a person can hide in.
        if r.random() < 0.7:
            self.place_wall(item('hide_closet'))
        self.place_wall(item('coat_rack'), corner=True)
        self.place_wall(item('shoe_rack'))
        self.place_wall(item('cabinet'))
        self.corners('big_plant', 1)

    def room_lobby(self, fabric, r):
        self.place_wall(item('mailboxes'))
        self.place_wall(item('sofa', fabric))
        self.corners('big_plant', 2)

    def room_storage(self, fabric, r):
        self.place_wall(item('hide_closet'))
        self.place_wall(item('planks'))
        self.place_wall(item('rack'))
        self.place_wall(item('rack'))
        self.place_wall(item('crates'))
        self.place_wall(item('crates'))
        self.place_wall(item('locker'))

    def room_basement(self, fabric, r):
        self.place_wall(item('hide_closet'))
        self.place_wall(item('hide_closet'))
        self.place_wall(item('planks'))
        self.place_wall(item('rack'))
        self.place_wall(item('rack'))
        self.place_wall(item('crates'))
        self.place_wall(item('crates'))
        self.place_wall(item('washer'))
        self.place_wall(item('workbench'))

    def room_shed(self, fabric, r):
        self.place_wall(item('radio'))
        self.place_wall(item('hide_closet'))
        self.place_wall(item('planks'))
        self.place_wall(item('workbench'))
        self.place_wall(item('crates'))
        self.place_wall(item('rack'))

    def room_garage(self, fabric, r):
        self.place_wall(item('radio'))
        self.place_wall(item('planks'))
        self.place_wall(item('planks'))
        self.place_wall(item('workbench'))
        self.place_wall(item('hide_closet'))
        self.place_wall(item('rack'))
        self.place_wall(item('crates'))
        self.place_wall(item('crates'))

    def room_cafe(self, fabric, r):
        for _ in range(4):
            self.place_wall(item('booth'))
        self.place_rows(cafe_table())
        self.corners('big_plant', 2)

    def room_shop_kitchen(self, fabric, r):
        self.kitchen_run(['fridge', 'fridge', 'counter', 'stove', 'stove', 'counter', 'sink_counter'])
        self.place_wall(item('rack'))

    def room_restroom(self, fabric, r):
        self.place_wall(item('toilet'))
        self.place_wall(item('toilet'))
        self.place_wall(item('sink'))
        self.place_wall(item('sink'))
