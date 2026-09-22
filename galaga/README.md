# Star Swarm — a Galaga-style shooter

A browser clone of Namco's *Galaga*, written in TypeScript with no runtime dependencies. Everything is drawn on a
canvas from pixel art defined in code, and every sound is synthesised with Web Audio, so there are no asset files.

## Run it

```bash
cd galaga
npm install
npm run dev        # http://localhost:5173
```

`npm run build` type-checks and writes a static build to `dist/` (relative URLs, so it can be served from any path).
`npm test` runs the headless simulation tests.

## Controls

| Action | Keyboard | Touch / mouse |
| --- | --- | --- |
| Move | Arrow keys or A / D | Hold and drag; the ship follows your finger |
| Fire | Space, Z, X or J (hold for auto-fire) | Fires automatically while held |
| Start | Enter (or fire) | Tap |
| Pause | P or Esc | |
| Mute | M | |

## What is in it

- The 40-enemy formation (4 bosses, 16 butterflies, 20 bees) flying in along looping entry paths in five groups,
  swaying while it fills and "breathing" once it is complete.
- Dive-bombing attacks that speed up and multiply with each stage, enemies firing aimed shots while diving, and bosses
  diving with butterfly escorts.
- Bosses take two hits (green, then purple).
- **Tractor beam:** a boss parks and fires a beam; get caught and your fighter is captured and rides above the boss.
  Shoot that boss while it is diving to free the fighter, which docks beside you as a **dual fighter** with twice the
  firepower (and twice the target). Shoot the boss while it sits in formation and the captured fighter is lost.
- **Challenging stages** (3, 7, 11, ...): 40 enemies fly through without shooting; 100 points per hit, 10,000 for all 40.
- Arcade scoring (50/100 bees, 80/160 butterflies, 150 and 400/800/1600 bosses), extra ships at 20,000 and every
  70,000 after that, a persistent high score, stage badges and the end-of-game hit-miss ratio.

## Layout

| File | Purpose |
| --- | --- |
| `src/game.ts` | The whole simulation: phases, formation, attacks, beam/capture/rescue, collisions, scoring. No DOM access. |
| `src/paths.ts` | Bezier path builder (constant-speed travel, arcs, mirroring) and every flight pattern. |
| `src/waves.ts` | Which enemy flies which entry path, and when, for normal and challenging stages. |
| `src/renderer.ts`, `src/sprites.ts` | Canvas drawing and the pixel art. |
| `src/audio.ts` | Web Audio sound effects. |
| `src/input.ts`, `src/main.ts` | Keyboard and pointer input, the fixed-step (60 Hz) game loop. |
| `tests/` | Vitest tests that drive the simulation headlessly with a seeded RNG. |

The simulation is deliberately separate from the canvas and audio: `Game` takes an RNG, a sound sink and a score
store, so the tests can play whole stages, including a capture and rescue, without a browser.
