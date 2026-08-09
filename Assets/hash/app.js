// hash - the page half of the terminal.
//
// It draws and it forwards. Every decision that needs to know something about the game - which commands exist, what
// arguments they take, which mod supplied them, how a line ranks, what a command printed - is made in C# and arrives
// as ready-made markup. The reasons are in Workspace/docs/Hash/PLAN.md; the short version is that Jint aborts a
// handler after 250 ms and fuzzy-matching a thousand item ids per keystroke would live uncomfortably close to that,
// while s1.call is synchronous in the same frame and hands the budget back afterwards.
//
// The one rule that shapes the whole file: any text change rebuilds the entire page at about half a millisecond per
// box. So the transcript and the suggestion block are ONE box each - a single text leaf with <br> and <span> - and
// the page is eight boxes rather than seventy. Adding a div per line would tax every keystroke with a rebuild that
// line had no part in.

const $ = (id) => document.getElementById(id);

/** Text that is about to become markup. The engine's inline compiler parses what we hand it, so "give <item>" would
 *  lose its argument to an unknown tag. */
const esc = (s) => String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

/** Cut a line to the width of the screen.
 *
 *  The transcript does not wrap - it is `white-space: pre`, because the columns are spaces - so a long line does not
 *  fold, it keeps going. And it keeps going past the edge of the phone and out onto the game world, because the
 *  clip is computed from a box the text has already outgrown. Cutting here is what the renderer will not do.
 *
 *  Only the drawn copy is shortened. The host still holds the whole line, so `grep` and `copy` see all of it. */
const clip = (s) => {
  const text = String(s ?? '');
  return text.length <= Terminal.Columns ? text : text.slice(0, Terminal.Columns - 2) + '..';
};

class Terminal {
  #input = $('input');
  #scroll = $('scroll');
  #suggest = $('suggest');
  #session = $('session');
  #count = $('count');
  #prompt = $('prompt');
  #ver = $('ver');
  #mark = $('mark');
  #term = document.querySelector('.term');

  /** The whole transcript, oldest first. Only the tail is ever rendered - the engine has no virtualisation and no
   *  readable scroll offset, so the window is ours to manage. */
  #lines = [];

  /** What the host last said about the prompt: the suggestion block, the inline completion, the selected row. */
  #state = { suggest: '', locked: false };

  /** Set while the page itself is writing to the field, so the resulting `input` event does not ask the host to
   *  complete a value the player did not type. */
  #echoing = false;

  /** Whether the log view is on, so the ticker knows to stay quiet. */
  #live = false;

  /** How many lines back the view is scrolled. 0 is the bottom, which is where it belongs almost all of the time.
   *
   *  Without this the terminal has no scrollback at all: an output taller than the screen - `help all`, `history`,
   *  a command that printed thirty lines - shows its tail and the beginning is simply unreachable. */
  #back = 0;

  start() {
    const boot = this.#host('boot', '');
    this.#session.textContent = boot.session ?? 'session:?';
    this.#count.textContent = `${boot.commands ?? 0} commands`;
    this.#prompt.textContent = boot.prompt ?? 'hash $';

    // Read rather than written into the page: a title bar still claiming 1.0 after an update is the kind of small
    // wrongness that makes a bug report point at the wrong version.
    //
    // The v is not decoration. In this pixel font a 1 is a bare vertical stroke, so "hash 1.0.0" reads as a title,
    // a pipe and a number - which is what it was mistaken for.
    if (boot.version) this.#ver.textContent = 'v' + boot.version;
    this.#showMark(boot);
    this.#face(boot);
    this.#live = boot.live === true;

    // Locked marks the header and is enforced by the host per command - the field stays usable, because looking
    // things up is exactly what a client came for and a disabled field would take that away too.
    if (boot.locked) {
      this.#term.classList.add('readonly');
      this.#state.locked = true;
    }

    for (const line of boot.banner ?? []) this.#lines.push(line);
    this.#renderScroll();

    this.#input.addEventListener('input', () => {
      if (this.#echoing) return;
      this.#refresh();
    });

    this.#input.addEventListener('keydown', (e) => this.#key(e));

    // The mouse wheel over the transcript, which is how anyone reaches for scrollback first. Three lines a notch,
    // the same step a browser uses.
    this.#scroll.addEventListener('wheel', (e) => {
      this.#scrollBy(e.wheelDelta > 0 ? 3 : -3);
      e.preventDefault();
    });

    // The log view, once a second and only while it is on.
    //
    // Polled rather than pushed: a line arriving from another mod would otherwise rebuild the page the moment it
    // was logged, several times a second, while the player is trying to type through it. A second is fast enough
    // to read as live and costs one rebuild - and the host answers with nothing at all when `logs` is off.
    setInterval(() => this.#drain(), 1000);

    // Escape and right-click both arrive here, and they mean different things.
    //
    // Right-click leaves. Where to is the host's call, not this file's: the console key fetched the phone out of a
    // pocket, so one press should put it back, while an icon press happened on a phone already in hand and the home
    // screen is the way back from there. `handled` false means the host wants its own close, so the press is left
    // alone and the app lands on the home screen like any other app.
    //
    // Escape dismisses the suggestion block. Only Escape: an empty prompt lists every command the game has, so a
    // handler that took both spent a right-click on a block the player never opened.
    document.addEventListener('back', (e) => {
      if (e.source === 'rightClick') {
        if (this.#host('back', '').handled) e.preventDefault();
        return;
      }

      if (!this.#state.suggest) return;

      this.#clearSuggestions();
      e.preventDefault();
    });

    // Focus twice, for two different moments. This one covers the first open: the host grants it after the render
    // that creates the field, because scripts run before anything is painted.
    this.#input.focus();

    // And this one covers every reopen. The page is not rebuilt when the app is shown again - the panel is just
    // switched back on - so nothing here would run a second time without the host saying so.
    s1.on('shown', () => this.#input.focus());
  }

  // ---------------------------------------------------------------------------------------------- keyboard --

  #key(e) {
    switch (e.key) {
      case 'Enter':
        if (e.repeat) return;
        this.#submit();
        return;

      case 'Tab':
        if (e.repeat) return;             // one completion per press, however long the key is held
        this.#move('accept');
        return;

      case 'ArrowUp': this.#move('up'); return;
      case 'ArrowDown': this.#move('down'); return;
      // Windows deletes a word with Ctrl+Backspace; TMP has no such thing and would take a single character, so
      // the page does it and the renderer keeps TMP off the key for as long as Ctrl is down.
      case 'Backspace': if (e.ctrlKey) this.#setLine(this.#input.value.replace(/\s*\S+\s*$/, '')); return;
      case 'Delete': if (e.ctrlKey) this.#setLine(this.#input.value.replace(/^\s*\S+\s*/, '')); return;

      // Shift is the scrollback modifier every terminal emulator uses; without it the key walks the suggestion
      // list, which is the more common thing to want while typing.
      case 'PageUp': if (e.shiftKey) this.#scrollBy(Terminal.Shown - 2); else this.#move('pageup'); return;
      case 'PageDown': if (e.shiftKey) this.#scrollBy(-(Terminal.Shown - 2)); else this.#move('pagedown'); return;
    }

    if (!e.ctrlKey) return;

    switch (e.key) {
      case 'l': this.#run('clear'); return;   // through the host, or grep would still see it
      case 'u': this.#setLine(''); return;
      case 'w': this.#setLine(this.#input.value.replace(/\s*\S+\s*$/, '')); return;
      // Copy when something is selected, interrupt when nothing is - which is what a Windows terminal does, and
      // the field has already put the selection on the clipboard by the time this runs.
      case 'c': if (!e.hasSelection) this.#cancel(); return;
      case 'r': this.#move('search'); return;
      case 'd': s1.call('close', ''); return;   // EOF, the way a shell reads it
    }
  }

  /** Every navigation key means the same thing to the host: here is the line, here is what I pressed, tell me what
   *  to show. Keeping the selection in C# is what stops the page and the engine disagreeing about which row is
   *  highlighted. */
  #move(action) {
    const reply = this.#host('nav', JSON.stringify({ line: this.#input.value, action }));

    // `false`: the host already told us what to show, and asking it again as if the player had typed the recalled
    // line would reset the very walk that produced it - so Up would hand back the newest entry over and over.
    if (typeof reply.line === 'string' && reply.line !== this.#input.value) this.#setLine(reply.line, false);
    this.#apply(reply);
  }

  #cancel() {
    if (this.#input.value) this.#lines.push({ cls: 'dim', text: `${this.#prompt.textContent} ${this.#input.value}^C` });

    this.#setLine('');
    this.#renderScroll();
  }

  // ------------------------------------------------------------------------------------------------ submit --

  #submit() {
    const line = this.#input.value.trim();
    if (!line) return;

    this.#setLine('');
    this.#clearSuggestions();
    this.#run(line);
  }

  /** Send a line to the host and draw what came back. `clear` answers with nothing, which empties the screen. */
  #run(line) {
    this.#back = 0;
    const reply = this.#host('run', line);

    this.#lines = reply.cleared ? [] : this.#lines;
    for (const out of reply.lines ?? []) this.#lines.push(out);

    if (typeof reply.commands === 'number') this.#count.textContent = `${reply.commands} commands`;

    // `logs` may have just been switched on or off, and the host says so with every answer rather than the page
    // parsing the line to find out.
    this.#live = reply.live === true;
    this.#face(reply);
    this.#showMark(reply);

    this.#renderScroll();
  }

  /** The typeface the host says to draw in. A class on the root, so one CSS rule carries the whole change. */
  #face(reply) {
    if (!reply || !reply.font) return;

    const term = document.querySelector('.term');
    if (!term) return;

    const pixel = reply.font === 'pixel';
    if (pixel) term.classList.add('pixel');
    else term.classList.remove('pixel');
  }

  // ------------------------------------------------------------------------------------------------ render --

  /** Move the view through the buffer. Clamped at both ends, and it never scrolls past what the page kept. */
  #scrollBy(lines) {
    const room = this.#room();
    const most = Math.max(0, this.#lines.length - room);

    const wanted = Math.min(most, Math.max(0, this.#back + lines));
    if (wanted === this.#back) return;

    this.#back = wanted;
    this.#renderScroll();
  }

  /** What `#` points at, in the header - or nothing at all when nothing is marked, because an empty label is
   *  quieter than the word "none" sitting there all session. */
  #showMark(reply) {
    if (typeof reply.mark !== 'string') return;

    this.#mark.textContent = reply.mark;
  }

  /** Pull in whatever the game logged on its own since the last tick. */
  #drain() {
    if (!this.#live) return;

    const reply = this.#host('drain', '');
    if (!reply.lines || reply.lines.length === 0) return;

    for (const line of reply.lines) this.#lines.push(line);

    // Stay where the eye is. Without this a line arriving while the player reads scrollback shifts the whole view
    // up by one, because the offset is counted from the end.
    if (this.#back > 0) this.#back += reply.lines.length;

    this.#renderScroll();
  }

  /** Ask the host what the current line should look like, and draw the answer. */
  #refresh() {
    this.#apply(this.#host('nav', JSON.stringify({ line: this.#input.value, action: 'typed' })));
  }

  #apply(reply) {
    this.#back = 0;
    this.#showMark(reply);
    this.#state.suggest = reply.suggest ?? '';
    this.#suggest.innerHTML = this.#state.suggest;

    // The inline suggestion, drawn behind the caret by the renderer. Only the REMAINDER goes here - the host sends
    // "ve" for a typed "gi" - and it is empty unless the highlighted row actually continues what was typed.
    this.#input.setAttribute('data-ghost', reply.ghost ?? '');

    // The suggestion block took rows away from the transcript, so redraw it shorter. The engine will not do this
    // for us: a flex child with min-height: 0 still refuses to give way here, and the two blocks end up drawn on
    // top of each other. Counting the rows and sizing the tail to what is left is deterministic and needs no
    // overflow behaviour at all - which suits a terminal, where the tail is the part anyone reads.
    this.#renderScroll();
  }

  #clearSuggestions() {
    this.#state.suggest = '';
    this.#suggest.innerHTML = '';
  }

  /** Write to the field without asking the host to complete what we just wrote.
   *
   *  `refresh` is false when the text came from the host: it already knows, and telling it again as if the player
   *  had typed would throw away whatever walk it is in the middle of. */
  #setLine(text, refresh = true) {
    this.#echoing = true;
    this.#input.value = text;
    this.#echoing = false;

    if (refresh) this.#refresh();
  }

  /** The transcript as a single text leaf, tail first and only as much of it as there is room for. The host keeps
   *  the rest, where `grep` and `history` can still reach it. */
  #renderScroll() {
    if (this.#lines.length > Terminal.Kept) this.#lines = this.#lines.slice(-Terminal.Kept);

    const room = this.#room();
    if (this.#back > Math.max(0, this.#lines.length - room)) this.#back = Math.max(0, this.#lines.length - room);

    const end = this.#lines.length - this.#back;
    const shown = this.#lines.slice(Math.max(0, end - room), end);

    const rows = shown.map(({ cls, text }) => {
      const cut = clip(text);
      return cls ? `<span class="${cls}">${esc(cut)}</span>` : esc(cut);
    });

    // Say how far down the buffer goes. A scrolled-up terminal that looks exactly like a terminal at the bottom is
    // how people end up thinking output went missing.
    if (this.#back > 0) rows.push(`<span class="dim">-- ${this.#back} more below --</span>`);

    this.#scroll.innerHTML = rows.join('<br>');
  }

  /** Lines the transcript may draw right now. The suggestion block takes its rows off the top. */
  #room() {
    return Math.max(Terminal.MinLines, Terminal.Shown - this.#suggestRows());
  }

  /** Rows the suggestion block occupies right now. One <br> per row boundary, so rows are breaks plus one. */
  #suggestRows() {
    const markup = this.#state.suggest;
    if (!markup) return 0;

    return (markup.match(/<br>/g) ?? []).length + 1;
  }

  // -------------------------------------------------------------------------------------------------- host --

  /** Every call answers with JSON. A host that throws logs it and returns "", which would otherwise land here as a
   *  parse error on a line the player cannot do anything about. */
  #host(name, arg) {
    const raw = s1.call(name, arg);
    if (!raw) return {};

    try {
      return JSON.parse(raw);
    } catch (e) {
      console.error(`hash: ${name} answered with something that is not JSON:`, raw);
      return {};
    }
  }
}

/** Lines drawn at once.
 *
 *  Sized to FIT rather than to scroll. The viewport is 400 css px on its short side; take the terminal's padding,
 *  the header, the rule and the prompt row off that and about 22 lines of 15px remain. Drawing more does not give
 *  the player more - it pushes the prompt off the bottom of the phone, which is what forty did.
 *
 *  The host keeps far more than this (Transcript.Kept), and `grep` and `history` read from there, so nothing is
 *  actually lost by not drawing it. */
Terminal.Shown = 18;

/** Characters that fit across the landscape viewport at the terminal's fixed glyph advance: 733 css px less the
 *  padding, divided by 7. Two spare so a cut line never touches the edge. */
Terminal.Columns = 99;

/** Never draw fewer than this, however tall the suggestion block gets. A terminal that shows the menu and none of
 *  what just happened has lost the thread. */
Terminal.MinLines = 4;

/** Lines the page keeps in hand for a redraw. Above the drawn window so shrinking and growing the suggestion block
 *  does not permanently throw lines away, but far below what the host keeps. */
Terminal.Kept = 200;

new Terminal().start();
