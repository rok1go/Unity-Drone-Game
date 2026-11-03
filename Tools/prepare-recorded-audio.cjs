// Run after decoding the credited source recordings to .audio-work/*.wav.
// No synthesized layers: only filtering, trimming, loop crossfade and gain.
const fs = require('fs');
const path = require('path');
const root = path.resolve(__dirname, '..');

function readMono(file) {
  const bytes = fs.readFileSync(path.join(root, file));
  const channels = bytes.readUInt16LE(22);
  const rate = bytes.readUInt32LE(24);
  if (bytes.toString('ascii', 0, 4) !== 'RIFF' || bytes.readUInt16LE(34) !== 16)
    throw new Error('Expected decoded PCM16 WAV: ' + file);
  const frames = (bytes.length - 44) / (2 * channels);
  const data = new Float64Array(frames);
  for (let i = 0; i < frames; i++) {
    for (let c = 0; c < channels; c++) data[i] += bytes.readInt16LE(44 + (i * channels + c) * 2) / (32768 * channels);
  }
  return { rate, data };
}

function highPass(data, rate, hz) {
  const a = Math.exp(-2 * Math.PI * hz / rate);
  let previousInput = 0, previousOutput = 0;
  return Float64Array.from(data, x => {
    const y = a * (previousOutput + x - previousInput);
    previousInput = x; previousOutput = y;
    return y;
  });
}

function gain(data, targetRms, peakLimit) {
  let peak = 0, energy = 0;
  for (const x of data) { peak = Math.max(peak, Math.abs(x)); energy += x * x; }
  const rms = Math.sqrt(energy / data.length);
  const multiplier = Math.min(targetRms / Math.max(rms, 1e-9), peakLimit / Math.max(peak, 1e-9));
  return Float64Array.from(data, x => x * multiplier);
}

function writeWav(name, data, rate) {
  const bytes = Buffer.alloc(44 + data.length * 2);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVEfmt ', 8);
  bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22);
  bytes.writeUInt32LE(rate, 24); bytes.writeUInt32LE(rate * 2, 28);
  bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34);
  bytes.write('data', 36); bytes.writeUInt32LE(data.length * 2, 40);
  for (let i = 0; i < data.length; i++) bytes.writeInt16LE(Math.round(Math.max(-1, Math.min(1, data[i])) * 32767), 44 + i * 2);
  fs.writeFileSync(path.join(root, 'Assets/Resources/Audio', name), bytes);
  console.log(`${name}: ${(data.length / rate).toFixed(2)} s, ${rate} Hz, mono`);
}

const flight = readMono('.audio-work/FpvFlight3_Source.wav');
let filtered = highPass(highPass(flight.data, flight.rate, 70), flight.rate, 70);
const segment = filtered.slice(Math.round(4 * flight.rate), Math.round(9 * flight.rate));
const fade = Math.round(0.22 * flight.rate);
const loop = new Float64Array(segment.length - fade);
loop.set(segment.subarray(fade, segment.length - fade));
for (let i = 0; i < fade; i++) {
  const phase = i / (fade - 1) * Math.PI / 2;
  loop[loop.length - fade + i] = segment[segment.length - fade + i] * Math.cos(phase) + segment[i] * Math.sin(phase);
}
writeWav('DroneMotors.wav', gain(loop, 0.18, 0.84), flight.rate);

const blast = readMono('.audio-work/M67Explosion3_Source.wav');
let explosion = highPass(blast.data, blast.rate, 28);
let peak = 0;
for (const x of explosion) peak = Math.max(peak, Math.abs(x));
let onset = 0;
while (onset < explosion.length && Math.abs(explosion[onset]) < peak * 0.012) onset++;
explosion = explosion.slice(Math.max(0, onset - Math.round(0.003 * blast.rate)));
const edge = Math.round(0.001 * blast.rate), tail = Math.round(0.035 * blast.rate);
for (let i = 0; i < edge; i++) explosion[i] *= i / edge;
for (let i = 0; i < tail; i++) explosion[explosion.length - tail + i] *= 1 - i / tail;
// Preserve the real transient; raise only as far as the available peak headroom.
writeWav('DroneExplosion.wav', gain(explosion, 0.24, 0.95), blast.rate);
