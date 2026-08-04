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
  #term = document.querySelector('.term');

  /** The whole transcript, oldest first. Only the tail is ever rendered - the engine has no virtualisation and no
   *  readable scroll offset, so the window is ours to manage. */
  #lines = [];

  /** What the host last said about the prompt: the suggestion block, the inline completion, the selected row. */
  #state = { suggest: '', locked: false };

  /** Set while the page itself is writing to the field, so the resulting `input` event does not ask the host to
   *  complete a value the player did not type. */
  #echoing = false;

  start() {
    const boot = this.#host('boot', '');
    this.#session.textContent = boot.session ?? 'session:?';
    this.#count.textContent = `${boot.commands ?? 0} commands`;
    this.#prompt.textContent = boot.prompt ?? 'hash $';

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

    // The phone's back gesture and Escape both arrive here. A suggestion list open means Escape dismisses it; with
    // nothing open the app should close, so the event is left to the host.
    document.addEventListener('back', (e) => {
      if (!this.#state.suggest) return;

      this.#clearSuggestions();
      e.preventDefault();
    });

    this.#input.focus();
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
      case 'PageUp': this.#move('pageup'); return;
      case 'PageDown': this.#move('pagedown'); return;
    }

    if (!e.ctrlKey) return;

    switch (e.key) {
      case 'l': this.#run('clear'); return;   // through the host, or grep would still see it
      case 'u': this.#setLine(''); return;
      case 'w': this.#setLine(this.#input.value.replace(/\s*\S+\s*$/, '')); return;
      case 'c': this.#cancel(); return;
      case 'r': this.#move('search'); return;
    }
  }

  /** Every navigation key means the same thing to the host: here is the line, here is what I pressed, tell me what
   *  to show. Keeping the selection in C# is what stops the page and the engine disagreeing about which row is
   *  highlighted. */
  #move(action) {
    const reply = this.#host('nav', JSON.stringify({ line: this.#input.value, action }));

    if (typeof reply.line === 'string' && reply.line !== this.#input.value) this.#setLine(reply.line);
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
    const reply = this.#host('run', line);

    this.#lines = reply.cleared ? [] : this.#lines;
    for (const out of reply.lines ?? []) this.#lines.push(out);

    if (typeof reply.commands === 'number') this.#count.textContent = `${reply.commands} commands`;

    this.#renderScroll();
  }

  // ------------------------------------------------------------------------------------------------ render --

  /** Ask the host what the current line should look like, and draw the answer. */
  #refresh() {
    this.#apply(this.#host('nav', JSON.stringify({ line: this.#input.value, action: 'typed' })));
  }

  #apply(reply) {
    this.#state.suggest = reply.suggest ?? '';
    this.#suggest.innerHTML = this.#state.suggest;

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

  /** Write to the field without asking the host to complete what we just wrote. */
  #setLine(text) {
    this.#echoing = true;
    this.#input.value = text;
    this.#echoing = false;

    this.#refresh();
  }

  /** The transcript as a single text leaf, tail first and only as much of it as there is room for. The host keeps
   *  the rest, where `grep` and `history` can still reach it. */
  #renderScroll() {
    if (this.#lines.length > Terminal.Kept) this.#lines = this.#lines.slice(-Terminal.Kept);

    const room = Math.max(Terminal.MinLines, Terminal.Shown - this.#suggestRows());

    this.#scroll.innerHTML = this.#lines
      .slice(-room)
      .map(({ cls, text }) => {
        const cut = clip(text);
        return cls ? `<span class="${cls}">${esc(cut)}</span>` : esc(cut);
      })
      .join('<br>');
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
Terminal.Kept = 60;

new Terminal().start();
