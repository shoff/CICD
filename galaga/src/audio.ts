import type { SoundName, SoundSink } from './types';

/** Tiny Web Audio synthesiser; every effect is generated, so there are no sound files to ship. */
export class Synth implements SoundSink {
  muted = false;
  private ctx: AudioContext | null = null;
  private master: GainNode | null = null;
  private noiseBuffer: AudioBuffer | null = null;

  /** Browsers only allow audio after a user gesture, so call this from an input handler. */
  unlock(): void {
    if (!this.ctx) {
      try {
        this.ctx = new AudioContext();
      } catch {
        return;
      }
      this.master = this.ctx.createGain();
      this.master.gain.value = 0.3;
      this.master.connect(this.ctx.destination);
      const length = this.ctx.sampleRate;
      this.noiseBuffer = this.ctx.createBuffer(1, length, this.ctx.sampleRate);
      const data = this.noiseBuffer.getChannelData(0);
      for (let i = 0; i < length; i++) data[i] = Math.random() * 2 - 1;
    }
    if (this.ctx.state === 'suspended') void this.ctx.resume();
  }

  play(name: SoundName): void {
    if (!this.ctx || this.muted) return;
    switch (name) {
      case 'shoot':
        this.tone(1100, 220, 0.09, 'square', 0.12);
        break;
      case 'enemyDie':
        this.noise(0.18, 0.3, 2500);
        this.tone(500, 80, 0.14, 'square', 0.1);
        break;
      case 'bossHit':
        this.tone(660, 990, 0.06, 'square', 0.12);
        this.tone(990, 660, 0.06, 'square', 0.12, 0.06);
        break;
      case 'bossDie':
        this.noise(0.35, 0.35, 1600);
        this.tone(320, 50, 0.35, 'sawtooth', 0.12);
        break;
      case 'playerDie':
        this.noise(1.1, 0.45, 900);
        this.tone(220, 30, 1, 'sawtooth', 0.15);
        break;
      case 'dive':
        this.tone(1500, 300, 0.7, 'sine', 0.05);
        break;
      case 'beam':
        this.warble(3.2);
        break;
      case 'capture':
        [880, 740, 660, 554, 440, 370].forEach((f, i) => this.tone(f, f, 0.16, 'square', 0.08, i * 0.18));
        break;
      case 'rescue':
        [523, 659, 784, 1047, 784, 1047].forEach((f, i) => this.tone(f, f, 0.1, 'square', 0.08, i * 0.1));
        break;
      case 'stageStart':
        [523, 659, 784, 1047, 988, 1047].forEach((f, i) => this.tone(f, f, 0.12, 'square', 0.07, i * 0.13));
        break;
      case 'extraLife':
        for (let i = 0; i < 6; i++) this.tone(1320, 1760, 0.07, 'square', 0.07, i * 0.09);
        break;
      case 'perfect':
        [784, 988, 1175, 1568, 1175, 1568].forEach((f, i) => this.tone(f, f, 0.14, 'square', 0.08, i * 0.15));
        break;
    }
  }

  private tone(from: number, to: number, dur: number, type: OscillatorType, vol: number, delay = 0): void {
    const ctx = this.ctx;
    if (!ctx || !this.master) return;
    const t = ctx.currentTime + delay;
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.type = type;
    osc.frequency.setValueAtTime(from, t);
    osc.frequency.exponentialRampToValueAtTime(Math.max(to, 1), t + dur);
    gain.gain.setValueAtTime(vol, t);
    gain.gain.exponentialRampToValueAtTime(0.0001, t + dur);
    osc.connect(gain).connect(this.master);
    osc.start(t);
    osc.stop(t + dur + 0.02);
  }

  private noise(dur: number, vol: number, cutoff: number): void {
    const ctx = this.ctx;
    if (!ctx || !this.master || !this.noiseBuffer) return;
    const t = ctx.currentTime;
    const src = ctx.createBufferSource();
    src.buffer = this.noiseBuffer;
    const filter = ctx.createBiquadFilter();
    filter.type = 'lowpass';
    filter.frequency.setValueAtTime(cutoff, t);
    filter.frequency.exponentialRampToValueAtTime(100, t + dur);
    const gain = ctx.createGain();
    gain.gain.setValueAtTime(vol, t);
    gain.gain.exponentialRampToValueAtTime(0.0001, t + dur);
    src.connect(filter).connect(gain).connect(this.master);
    src.start(t);
    src.stop(t + dur + 0.02);
  }

  private warble(dur: number): void {
    const ctx = this.ctx;
    if (!ctx || !this.master) return;
    const t = ctx.currentTime;
    const osc = ctx.createOscillator();
    const lfo = ctx.createOscillator();
    const depth = ctx.createGain();
    const gain = ctx.createGain();
    osc.type = 'triangle';
    osc.frequency.value = 420;
    lfo.frequency.value = 11;
    depth.gain.value = 160;
    lfo.connect(depth).connect(osc.frequency);
    gain.gain.setValueAtTime(0.0001, t);
    gain.gain.exponentialRampToValueAtTime(0.09, t + 0.3);
    gain.gain.setValueAtTime(0.09, t + dur - 0.4);
    gain.gain.exponentialRampToValueAtTime(0.0001, t + dur);
    osc.connect(gain).connect(this.master);
    osc.start(t);
    lfo.start(t);
    osc.stop(t + dur);
    lfo.stop(t + dur);
  }
}
