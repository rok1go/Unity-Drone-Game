"""Minimal MagicaVoxel (.vox, version 150/200) reader with scene-graph transforms."""
import struct

IDENTITY = ((1, 0, 0), (0, 1, 0), (0, 0, 1))


class VoxFile:
    def __init__(self, path):
        data = open(path, 'rb').read()
        if data[:4] != b'VOX ':
            raise ValueError('Not a MagicaVoxel file: ' + path)
        self._d = data
        self.sizes, self.models, self.nodes = [], [], {}
        self.palette = None
        p = 8
        n, m = struct.unpack('<ii', data[p + 4:p + 12])  # MAIN
        p += 12 + n
        end = p + m
        while p < end:
            cid = data[p:p + 4].decode('ascii')
            n, m = struct.unpack('<ii', data[p + 4:p + 12])
            cp = p + 12
            if cid == 'SIZE':
                self.sizes.append(struct.unpack('<iii', data[cp:cp + 12]))
            elif cid == 'XYZI':
                count = struct.unpack('<i', data[cp:cp + 4])[0]
                self.models.append(data[cp + 4:cp + 4 + 4 * count])
            elif cid == 'RGBA':
                self.palette = bytearray(data[cp:cp + 1024])
            elif cid == 'nTRN':
                nid = struct.unpack('<i', data[cp:cp + 4])[0]
                attrs, q = self._dict(cp + 4)
                child = struct.unpack('<i', data[q:q + 4])[0]
                frame, _ = self._dict(q + 16)
                self.nodes[nid] = ('T', attrs, child, frame)
            elif cid == 'nGRP':
                nid = struct.unpack('<i', data[cp:cp + 4])[0]
                _, q = self._dict(cp + 4)
                k = struct.unpack('<i', data[q:q + 4])[0]
                self.nodes[nid] = ('G', struct.unpack('<%di' % k, data[q + 4:q + 4 + 4 * k]))
            elif cid == 'nSHP':
                nid = struct.unpack('<i', data[cp:cp + 4])[0]
                _, q = self._dict(cp + 4)
                self.nodes[nid] = ('S', struct.unpack('<i', data[q + 4:q + 8])[0])
            p = cp + n + m
        if self.palette is None:
            raise ValueError('The .vox file has no RGBA palette chunk.')

    def _str(self, p):
        n = struct.unpack('<i', self._d[p:p + 4])[0]
        return self._d[p + 4:p + 4 + n].decode('utf-8', 'replace'), p + 4 + n

    def _dict(self, p):
        k = struct.unpack('<i', self._d[p:p + 4])[0]
        p += 4
        out = {}
        for _ in range(k):
            key, p = self._str(p)
            value, p = self._str(p)
            out[key] = value
        return out, p

    @staticmethod
    def rotation(code):
        r = int(code) if code else 4  # 4 is the identity rotation byte
        i0, i1 = r & 3, (r >> 2) & 3
        i2 = 3 - i0 - i1
        signs = [-1 if (r >> b) & 1 else 1 for b in (4, 5, 6)]
        m = [[0] * 3 for _ in range(3)]
        m[0][i0], m[1][i1], m[2][i2] = signs
        return tuple(tuple(row) for row in m)

    def instances(self):
        """Visible shape instances in the scene graph: dict(model, rot, t, name)."""
        out = []

        def mul(a, b):
            return tuple(tuple(sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)) for i in range(3))

        def apply(a, v):
            return [sum(a[i][k] * v[k] for k in range(3)) for i in range(3)]

        def walk(nid, rot, t, name):
            node = self.nodes[nid]
            if node[0] == 'T':
                attrs, child, frame = node[1], node[2], node[3]
                if attrs.get('_hidden') == '1':
                    return
                local_t = [int(x) for x in frame.get('_t', '0 0 0').split()]
                nt = [a + b for a, b in zip(apply(rot, local_t), t)]
                walk(child, mul(rot, self.rotation(frame.get('_r'))), nt, attrs.get('_name') or name)
            elif node[0] == 'G':
                for c in node[1]:
                    walk(c, rot, t, name)
            else:
                out.append(dict(model=node[1], rot=rot, t=t, name=name))

        walk(0, IDENTITY, [0, 0, 0], '')
        return out

    def voxels(self, inst):
        """Yield (x, y, z, color) in MagicaVoxel world coordinates (z up)."""
        sx, sy, sz = self.sizes[inst['model']]
        r, t = inst['rot'], inst['t']
        data = self.models[inst['model']]
        for i in range(0, len(data), 4):
            # Doubled, centred coordinates keep the pivot exact for any rotation.
            cx, cy, cz = 2 * data[i] + 1 - sx, 2 * data[i + 1] + 1 - sy, 2 * data[i + 2] + 1 - sz
            yield (t[0] + (r[0][0] * cx + r[0][1] * cy + r[0][2] * cz - 1) // 2,
                   t[1] + (r[1][0] * cx + r[1][1] * cy + r[1][2] * cz - 1) // 2,
                   t[2] + (r[2][0] * cx + r[2][1] * cy + r[2][2] * cz - 1) // 2,
                   data[i + 3])

    def used_colors(self):
        used = set()
        for m in self.models:
            used.update(m[3::4])
        return used
