import { H, PLAYER_Y, SCALE, W } from './constants';
import type { Game } from './game';
import { createSprites, type SpriteSet } from './sprites';
import type { Enemy } from './types';

const FONT = '"Press Start 2P", monospace';
const STAR_COLORS = ['#ffffff', '#ff5555', '#55ff55', '#5599ff', '#ffff55', '#ff55ff', '#55ffff'];

interface Star {
  x: number;
  y: number;
  speed: number;
  color: string;
  blink: number;
}

export interface Overlay {
  paused: boolean;
  muted: boolean;
}

type Align = 'left' | 'center' | 'right';

export class Renderer {
  private readonly ctx: CanvasRenderingContext2D;
  private readonly sprites: SpriteSet;
  private readonly stars: Star[] = [];
  private clock = 0;

  constructor(canvas: HTMLCanvasElement) {
    canvas.width = W * SCALE;
    canvas.height = H * SCALE;
    const ctx = canvas.getContext('2d');
    if (!ctx) throw new Error('2D canvas is not available');
    this.ctx = ctx;
    this.sprites = createSprites();
    for (let i = 0; i < 70; i++) {
      this.stars.push({
        x: Math.random() * W,
        y: Math.random() * H,
        speed: 12 + Math.random() * 36,
        color: STAR_COLORS[i % STAR_COLORS.length],
        blink: Math.random() * Math.PI * 2,
      });
    }
  }

  draw(game: Game, dt: number, overlay: Overlay): void {
    const ctx = this.ctx;
    this.clock += dt;
    ctx.setTransform(SCALE, 0, 0, SCALE, 0, 0);
    ctx.imageSmoothingEnabled = false;
    ctx.fillStyle = '#000';
    ctx.fillRect(0, 0, W, H);

    this.drawStars(dt, game.phase !== 'gameOver' && (game.phase === 'title' || game.player.alive));

    if (game.phase === 'title') {
      this.drawTitle(game);
    } else {
      this.drawBeams(game);
      for (const e of game.enemies) this.drawEnemy(game, e);
      this.drawCaptured(game);
      this.drawPlayer(game);
      this.drawBullets(game);
      this.drawEffects(game);
      this.drawMessages(game);
    }
    this.drawHud(game);

    if (overlay.paused) {
      ctx.fillStyle = 'rgba(0,0,0,0.55)';
      ctx.fillRect(0, 0, W, H);
      this.text('PAUSED', W / 2, H / 2 - 8, '#ffd400', 'center');
      this.text('P TO RESUME', W / 2, H / 2 + 6, '#ffffff', 'center');
    }
    if (overlay.muted) this.text('MUTE', W - 2, H - 22, '#777777', 'right');
  }

  private text(str: string, x: number, y: number, color: string, align: Align = 'left', size = 8): void {
    const ctx = this.ctx;
    ctx.font = `${size}px ${FONT}`;
    ctx.textAlign = align;
    ctx.textBaseline = 'top';
    ctx.fillStyle = color;
    ctx.fillText(str, x, y);
  }

  private sprite(img: HTMLCanvasElement, x: number, y: number, angle = 0): void {
    const ctx = this.ctx;
    if (angle === 0) {
      ctx.drawImage(img, Math.round(x - img.width / 2), Math.round(y - img.height / 2));
      return;
    }
    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(angle);
    ctx.drawImage(img, -img.width / 2, -img.height / 2);
    ctx.restore();
  }

  private drawStars(dt: number, moving: boolean): void {
    const ctx = this.ctx;
    for (const s of this.stars) {
      if (moving) {
        s.y += s.speed * dt;
        if (s.y > H) {
          s.y -= H;
          s.x = Math.random() * W;
        }
      }
      if (Math.sin(this.clock * 3 + s.blink) < -0.2) continue;
      ctx.fillStyle = s.color;
      ctx.fillRect(Math.round(s.x), Math.round(s.y), 1, 1);
    }
  }

  private enemyFrames(e: Enemy): [HTMLCanvasElement, HTMLCanvasElement] {
    switch (e.kind) {
      case 'bee':
        return this.sprites.bee;
      case 'butterfly':
        return this.sprites.butterfly;
      case 'boss':
        return e.hp < 2 && e.state !== 'challenge' ? this.sprites.bossHurt : this.sprites.boss;
    }
  }

  private drawEnemy(game: Game, e: Enemy): void {
    if (e.state === 'waiting') return;
    const flap = e.state === 'formation' ? Math.floor(game.time * 1.6) % 2 : Math.floor(game.time * 6 + e.id) % 2;
    this.sprite(this.enemyFrames(e)[flap], e.x, e.y, e.angle);
  }

  private drawBeams(game: Game): void {
    const ctx = this.ctx;
    const colors = ['#27e0ff', '#2f6bff', '#b44cff'];
    for (const e of game.enemies) {
      const level = game.beamLevel(e);
      if (level <= 0) continue;
      const top = e.y + 8;
      const full = PLAYER_Y + 12 - top;
      const bottom = top + full * level;
      ctx.globalAlpha = 0.85;
      for (let y = top; y < bottom; y += 2) {
        const f = (y - top) / full;
        const half = 4 + f * 22;
        // Bands of colour scroll down the beam.
        ctx.fillStyle = colors[(((Math.floor((y - game.time * 40) / 4)) % 3) + 3) % 3];
        ctx.fillRect(Math.round(e.x - half), Math.round(y), Math.round(half * 2), 1);
      }
      ctx.globalAlpha = 1;
    }
  }

  private drawCaptured(game: Game): void {
    const c = game.captured;
    if (!c) return;
    this.sprite(this.sprites.capturedFighter, c.x, c.y, c.angle);
  }

  private drawPlayer(game: Game): void {
    const p = game.player;
    if (!p.alive) return;
    this.sprite(this.sprites.fighter, p.x, PLAYER_Y);
    if (p.dual) this.sprite(this.sprites.fighter, p.x + 16, PLAYER_Y);
  }

  private drawBullets(game: Game): void {
    const ctx = this.ctx;
    for (const b of game.playerBullets) {
      ctx.fillStyle = '#ffffff';
      ctx.fillRect(Math.round(b.x) - 0.5, Math.round(b.y) - 3, 1, 6);
      ctx.fillStyle = '#ff2a2a';
      ctx.fillRect(Math.round(b.x) - 0.5, Math.round(b.y) - 3, 1, 2);
    }
    for (const b of game.enemyBullets) {
      ctx.fillStyle = '#ff2a2a';
      ctx.fillRect(Math.round(b.x) - 1, Math.round(b.y) - 2, 2, 4);
      ctx.fillStyle = '#ffffff';
      ctx.fillRect(Math.round(b.x) - 0.5, Math.round(b.y) - 1, 1, 2);
    }
  }

  private drawEffects(game: Game): void {
    const ctx = this.ctx;
    for (const pt of game.particles) {
      const size = pt.life / pt.maxLife > 0.5 ? 2 : 1;
      ctx.fillStyle = pt.color;
      ctx.fillRect(pt.x - size / 2, pt.y - size / 2, size, size);
    }
    for (const pop of game.popups) this.text(pop.text, pop.x, pop.y - 4, pop.color, 'center');
  }

  private drawHud(game: Game): void {
    const blink = Math.floor(this.clock * 2.5) % 2 === 0;
    if (game.phase !== 'title' || blink) this.text('1UP', 12, 2, '#ff2a2a');
    this.text(String(game.score).padStart(2, '0'), 60, 11, '#ffffff', 'right');
    this.text('HIGH SCORE', W / 2 + 16, 2, '#ff2a2a', 'center');
    this.text(String(game.high), W / 2 + 16, 11, '#ffffff', 'center');

    if (game.phase === 'title') return;
    const icons = Math.min(game.reserve, 7);
    for (let i = 0; i < icons; i++) this.sprite(this.sprites.fighter, 10 + i * 16, H - 9);
    this.drawBadges(game.stage);
  }

  private drawBadges(stage: number): void {
    const badges: HTMLCanvasElement[] = [];
    let n = stage;
    while (n >= 10) {
      badges.push(this.sprites.badge10);
      n -= 10;
    }
    while (n >= 5) {
      badges.push(this.sprites.badge5);
      n -= 5;
    }
    while (n >= 1) {
      badges.push(this.sprites.badge1);
      n -= 1;
    }
    let x = W - 4;
    for (const img of badges.slice(0, 12)) {
      x -= img.width + 1;
      this.ctx.drawImage(img, x, H - 2 - img.height);
    }
  }

  private drawMessages(game: Game): void {
    const mid = H / 2 - 16;
    switch (game.phase) {
      case 'stageIntro':
        if (game.stage === 1 && game.phaseTime < 1.2) {
          this.text('PLAYER 1', W / 2, mid, '#27e0ff', 'center');
        } else if (game.challenge) {
          this.text('CHALLENGING STAGE', W / 2, mid, '#27e0ff', 'center');
        } else {
          this.text(`STAGE ${game.stage}`, W / 2, mid, '#27e0ff', 'center');
        }
        break;
      case 'challengeResult':
        this.text('NUMBER OF HITS', W / 2 - 8, mid, '#27e0ff', 'center');
        this.text(String(game.challengeHits), W / 2 + 76, mid, '#ffffff', 'right');
        if (game.phaseTime > 1) {
          if (game.challengeHits === 40) {
            this.text('PERFECT !', W / 2, mid + 16, '#ff2a2a', 'center');
            this.text('SPECIAL BONUS 10000', W / 2, mid + 30, '#ffd400', 'center');
          } else {
            this.text('BONUS', W / 2 - 8, mid + 16, '#27e0ff', 'center');
            this.text(String(game.challengeBonus), W / 2 + 76, mid + 16, '#ffffff', 'right');
          }
        }
        break;
      case 'gameOver':
        if (game.phaseTime < 3) {
          this.text('GAME OVER', W / 2, mid, '#27e0ff', 'center');
        } else {
          const ratio = game.shotsFired > 0 ? ((game.hitCount / game.shotsFired) * 100).toFixed(1) : '0.0';
          this.text('- RESULTS -', W / 2, mid - 16, '#ff2a2a', 'center');
          this.text('SHOTS FIRED', 16, mid + 4, '#ffd400');
          this.text(String(game.shotsFired), W - 16, mid + 4, '#ffd400', 'right');
          this.text('NUMBER OF HITS', 16, mid + 18, '#ffd400');
          this.text(String(game.hitCount), W - 16, mid + 18, '#ffd400', 'right');
          this.text('HIT-MISS RATIO', 16, mid + 32, '#ffffff');
          this.text(`${ratio} %`, W - 16, mid + 32, '#ffffff', 'right');
        }
        break;
      default:
        break;
    }
    if (game.banner) this.text(game.banner.text, W / 2, mid + 16, game.banner.color, 'center');
  }

  private drawTitle(game: Game): void {
    const s = this.sprites;
    this.text('STAR', W / 2, 38, '#ffd400', 'center', 16);
    this.text('SWARM', W / 2, 58, '#ff2a2a', 'center', 16);
    this.text('A GALAGA-STYLE SHOOTER', W / 2, 82, '#27e0ff', 'center');

    this.text('- SCORE -', W / 2, 104, '#ffffff', 'center');
    this.text('FORMATION  DIVING', W / 2 + 20, 118, '#777777', 'center');
    const flap = Math.floor(game.time * 1.6) % 2;
    const rows: Array<[HTMLCanvasElement, string, string]> = [
      [s.bee[flap], '50', '100'],
      [s.butterfly[flap], '80', '160'],
      [s.boss[flap], '150', '400-1600'],
    ];
    rows.forEach(([img, a, b], i) => {
      const y = 134 + i * 18;
      this.sprite(img, 36, y + 3);
      this.text(a, 104, y, '#27e0ff', 'right');
      this.text(b, 204, y, '#27e0ff', 'right');
    });

    this.text('ARROWS/A D  MOVE', W / 2, 196, '#ffffff', 'center');
    this.text('SPACE/Z  FIRE', W / 2, 208, '#ffffff', 'center');
    this.text('P PAUSE  M MUTE', W / 2, 220, '#ffffff', 'center');
    this.text('TOUCH: DRAG TO MOVE', W / 2, 232, '#777777', 'center');
    if (Math.floor(this.clock * 2) % 2 === 0) this.text('PRESS ENTER OR TAP', W / 2, 254, '#ffd400', 'center');
  }
}
