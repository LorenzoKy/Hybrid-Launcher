(() => {
  const still = matchMedia('(prefers-reduced-motion: reduce)').matches;

  // Sections fade in as they scroll into view.
  const io = new IntersectionObserver(entries => entries.forEach(e => {
    if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); }
  }), { threshold: 0.12, rootMargin: '0px 0px -40px 0px' });
  document.querySelectorAll('.reveal').forEach((el, i) => {
    el.style.transitionDelay = `${(i % 4) * 60}ms`;
    io.observe(el);
  });

  // The header gets a hairline once the page scrolls under it.
  const top = document.querySelector('.top');
  const onScroll = () => top && top.classList.toggle('scrolled', scrollY > 8);
  addEventListener('scroll', onScroll, { passive: true });
  addEventListener('resize', onScroll);
  onScroll();

  // Feature tiles: a light follows the pointer and lights up the border near it.
  document.querySelectorAll('.tile').forEach(t => t.addEventListener('pointermove', e => {
    const r = t.getBoundingClientRect();
    t.style.setProperty('--mx', `${e.clientX - r.left}px`);
    t.style.setProperty('--my', `${e.clientY - r.top}px`);
  }));

  // Download buttons point straight at the newest release's Windows zip (falls back to the releases page).
  fetch('https://api.github.com/repos/LorenzoKy/Hybrid-Launcher/releases/latest')
    .then(r => r.ok ? r.json() : Promise.reject(r.status))
    .then(rel => {
      const zip = (rel.assets || []).find(a => /win-x64\.zip$/i.test(a.name));
      if (!zip) return;
      document.querySelectorAll('[data-download]').forEach(a => { a.href = zip.browser_download_url; });
      const line = document.getElementById('release-line');
      if (line) line.firstChild.textContent = `Version ${rel.tag_name.replace(/^v/, '')} · ${Math.round(zip.size / 1048576)} MB portable zip, no installer. `;
    })
    .catch(() => {});

  // Background: a quiet night scene in the logo's colours. Soft crimson and ivory glows drifting slowly, a few stars at
  // different depths, a faint grid floor that runs towards you from the horizon, and once in a while a streak of light. The
  // pointer and the scroll move the layers a little, at different speeds. It stays in the back: low contrast, nothing fast.
  // Drawn only while the page is visible.
  const c = document.getElementById('bg');
  const x = c.getContext('2d');
  let w = 0, h = 0, dpr = 1, raf = 0;
  let mx = 0, my = 0, tx = 0, ty = 0;              // pointer, eased
  const glows = [
    { col: [215, 20, 58], r: 0.46, px: 0.18, py: 0.24, sx: 0.00008, sy: 0.00006, a: 0.13, depth: 0.6 },
    { col: [244, 239, 228], r: 0.5, px: 0.84, py: 0.3, sx: 0.00006, sy: 0.00009, a: 0.045, depth: 0.9 },
    { col: [142, 12, 38], r: 0.36, px: 0.55, py: 0.08, sx: 0.0001, sy: 0.00007, a: 0.1, depth: 0.4 },
  ];
  const stars = Array.from({ length: 140 }, () => ({ x: Math.random(), y: Math.random(), z: 0.2 + Math.random() * 0.8, tw: Math.random() * 6.28 }));
  const streaks = [];

  const fit = () => {
    dpr = Math.min(devicePixelRatio || 1, 2);
    w = innerWidth; h = innerHeight;
    c.width = w * dpr; c.height = h * dpr;
    x.setTransform(dpr, 0, 0, dpr, 0, 0);
  };
  addEventListener('pointermove', e => { mx = e.clientX / w - 0.5; my = e.clientY / h - 0.5; }, { passive: true });

  const frame = now => {
    // A tab opened in the background can start at zero size: wait for a real one.
    if (!w || !h) { fit(); if (!w || !h) { if (!still) raf = requestAnimationFrame(frame); return; } }
    const t = now || 0;
    tx += (mx - tx) * 0.04; ty += (my - ty) * 0.04;
    const sc = Math.min(scrollY / h, 3);           // how many screens down we are

    // Sky
    const g = x.createLinearGradient(0, 0, 0, h);
    g.addColorStop(0, '#060607'); g.addColorStop(0.55, '#09080a'); g.addColorStop(1, '#0e0809');
    x.fillStyle = g; x.fillRect(0, 0, w, h);

    // Glows, drifting slowly and rising a little as you scroll
    x.globalCompositeOperation = 'lighter';
    for (const o of glows) {
      const cx = (o.px + Math.sin(t * o.sx * 6.28) * 0.1) * w - tx * 50 * o.depth;
      const cy = (o.py + Math.cos(t * o.sy * 6.28) * 0.08) * h - ty * 30 * o.depth - sc * 60 * o.depth;
      const r = o.r * Math.max(w, h);
      const rg = x.createRadialGradient(cx, cy, 0, cx, cy, r);
      rg.addColorStop(0, `rgba(${o.col}, ${o.a})`);
      rg.addColorStop(0.45, `rgba(${o.col}, ${o.a * 0.35})`);
      rg.addColorStop(1, `rgba(${o.col}, 0)`);
      x.fillStyle = rg;
      x.fillRect(0, 0, w, h);
    }
    x.globalCompositeOperation = 'source-over';

    // Stars: nearer ones move more with the pointer and the scroll, and twinkle gently
    for (const st of stars) {
      let sx = (st.x * w - tx * 30 * st.z) % w, sy = (st.y * h - ty * 24 * st.z - scrollY * 0.06 * st.z) % h;
      if (sx < 0) sx += w; if (sy < 0) sy += h;
      const a = 0.15 + 0.35 * st.z * (0.65 + 0.35 * Math.sin(t * 0.0015 + st.tw));
      x.fillStyle = `rgba(244, 239, 228, ${a.toFixed(3)})`;
      x.fillRect(sx, sy, st.z * 1.5, st.z * 1.5);
    }

    // A streak of light, now and then
    if (!still && Math.random() < 0.0025 && streaks.length < 1) streaks.push({ x: Math.random() * w * 0.8, y: Math.random() * h * 0.4, life: 0, hue: Math.random() < 0.6 ? '240, 70, 106' : '244, 239, 228' });
    for (let i = streaks.length - 1; i >= 0; i--) {
      const k = streaks[i]; k.life += 1;
      const p = k.life / 60, len = 140;
      const hx = k.x + p * 380, hy = k.y + p * 140;
      const sg = x.createLinearGradient(hx - len, hy - len * 0.38, hx, hy);
      sg.addColorStop(0, `rgba(${k.hue},0)`); sg.addColorStop(1, `rgba(${k.hue},${(0.5 * (1 - p)).toFixed(3)})`);
      x.strokeStyle = sg; x.lineWidth = 1.2;
      x.beginPath(); x.moveTo(hx - len, hy - len * 0.38); x.lineTo(hx, hy); x.stroke();
      if (k.life > 60) streaks.splice(i, 1);
    }

    // The grid floor: lines from a vanishing point on the horizon, and cross lines that run slowly towards you
    const hz = h * (0.74 + sc * 0.06) - ty * 10;   // the horizon sinks as you scroll down
    const vx = w * 0.5 - tx * 60;
    const fade = Math.max(0, 1 - sc * 0.6);
    if (fade > 0 && hz < h) {
      const glow = x.createRadialGradient(vx, hz, 0, vx, hz, w * 0.5);
      glow.addColorStop(0, `rgba(215, 20, 58, ${0.18 * fade})`);
      glow.addColorStop(0.4, `rgba(142, 12, 38, ${0.06 * fade})`);
      glow.addColorStop(1, 'rgba(0,0,0,0)');
      x.fillStyle = glow; x.fillRect(0, hz - w * 0.3, w, w * 0.6);

      x.save();
      x.beginPath(); x.rect(0, hz, w, h - hz); x.clip();
      const lg = x.createLinearGradient(0, hz, 0, h);
      lg.addColorStop(0, 'rgba(215, 20, 58, 0)');
      lg.addColorStop(0.3, `rgba(215, 40, 70, ${0.16 * fade})`);
      lg.addColorStop(1, `rgba(244, 239, 228, ${0.14 * fade})`);
      x.strokeStyle = lg; x.lineWidth = 1;
      x.beginPath();
      const n = 22;
      for (let i = -n; i <= n; i++) { x.moveTo(vx + i * 6, hz); x.lineTo(vx + i * (w / n) * 1.8, h); }
      const speed = still ? 0 : (t * 0.00022) % 1;
      for (let i = 0; i < 16; i++) {
        const z = (i + 1 - speed) / 16;              // 0 at the horizon, 1 at the bottom
        const y = hz + (h - hz) * Math.pow(z, 2.6);
        x.moveTo(0, y); x.lineTo(w, y);
      }
      x.stroke();
      x.restore();
      // A thin horizon line
      const hl = x.createLinearGradient(0, 0, w, 0);
      hl.addColorStop(0, 'rgba(255,255,255,0)'); hl.addColorStop(0.5, `rgba(255, 150, 165, ${0.35 * fade})`); hl.addColorStop(1, 'rgba(255,255,255,0)');
      x.fillStyle = hl; x.fillRect(0, hz - 0.5, w, 1);
    }

    // Darken under the content a bit further down the page, so text stays easy to read
    if (sc > 0.3) { x.fillStyle = `rgba(6, 6, 7, ${Math.min(0.45, (sc - 0.3) * 0.4)})`; x.fillRect(0, 0, w, h); }

    if (!still) raf = requestAnimationFrame(frame);
  };
  fit();
  addEventListener('resize', () => { fit(); if (still) frame(0); });
  if (still) addEventListener('scroll', () => frame(0), { passive: true });
  document.addEventListener('visibilitychange', () => {
    cancelAnimationFrame(raf);
    if (!document.hidden && !still) raf = requestAnimationFrame(frame);
  });
  frame(0);
})();
