import { W } from './constants';
import type { InputState } from './types';

const LEFT = ['ArrowLeft', 'KeyA'];
const RIGHT = ['ArrowRight', 'KeyD'];
const FIRE = ['Space', 'KeyZ', 'KeyX', 'KeyJ'];
const START = ['Enter', 'NumpadEnter'];
const CAPTURED = new Set([...LEFT, ...RIGHT, ...FIRE, ...START, 'ArrowUp', 'ArrowDown']);

/** Keyboard plus pointer (touch or mouse drag) input, sampled once per simulation step. */
export class Input {
  private readonly down = new Set<string>();
  private readonly pressed = new Set<string>();
  private pointerId: number | null = null;
  private pointerX: number | null = null;

  constructor(canvas: HTMLCanvasElement, onGesture: () => void) {
    window.addEventListener('keydown', (ev) => {
      if (CAPTURED.has(ev.code)) ev.preventDefault();
      onGesture();
      if (!ev.repeat) this.pressed.add(ev.code);
      this.down.add(ev.code);
    });
    window.addEventListener('keyup', (ev) => this.down.delete(ev.code));
    window.addEventListener('blur', () => this.down.clear());

    const toLogical = (clientX: number): number => {
      const rect = canvas.getBoundingClientRect();
      return ((clientX - rect.left) / rect.width) * W;
    };
    canvas.addEventListener('pointerdown', (ev) => {
      ev.preventDefault();
      onGesture();
      canvas.setPointerCapture(ev.pointerId);
      this.pointerId = ev.pointerId;
      this.pointerX = toLogical(ev.clientX);
      this.pressed.add('Pointer');
    });
    canvas.addEventListener('pointermove', (ev) => {
      if (ev.pointerId === this.pointerId) this.pointerX = toLogical(ev.clientX);
    });
    const release = (ev: PointerEvent): void => {
      if (ev.pointerId !== this.pointerId) return;
      this.pointerId = null;
      this.pointerX = null;
    };
    canvas.addEventListener('pointerup', release);
    canvas.addEventListener('pointercancel', release);
  }

  snapshot(): InputState {
    const any = (codes: string[], set: Set<string>): boolean => codes.some((c) => set.has(c));
    const touching = this.pointerId !== null;
    return {
      left: any(LEFT, this.down),
      right: any(RIGHT, this.down),
      // Holding a finger on the screen auto-fires.
      fire: any(FIRE, this.down) || touching,
      firePressed: any(FIRE, this.pressed) || this.pressed.has('Pointer'),
      start: any(START, this.pressed),
      pointerX: this.pointerX,
    };
  }

  /** Clears the edge-triggered presses once a simulation step has seen them. */
  endStep(): void {
    for (const code of [...this.pressed]) {
      if (code !== 'KeyP' && code !== 'KeyM' && code !== 'Escape') this.pressed.delete(code);
    }
  }

  /** Consumes a one-shot key press handled outside the simulation (pause, mute). */
  take(code: string): boolean {
    return this.pressed.delete(code);
  }
}
