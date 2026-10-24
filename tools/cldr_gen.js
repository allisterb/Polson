// Generates Polson's number-format table from Node's ICU (CLDR) data. Per locale: compact patterns per power of ten
// (10^3..10^14) for the plural categories "one" and "other", short and long, plain and per currency; the plain currency
// and percent patterns; separators and grouping; and which plural rule the locale follows (verified with
// Intl.PluralRules). A pattern is the formatted string with its number replaced by {n} (absent where CLDR spells the
// number out, as Italian "mille"), plus the divisor. Also writes oracle cases ICU itself formatted, for the tests.
//   node cldr_gen.js <out.json>
const fs = require('fs');

const LOCALES = ['en-US', 'en-GB', 'en-IN', 'en-AU', 'en-CA', 'en-SG', 'en-ZA', 'en-NG', 'hi-IN', 'ja-JP', 'zh-CN', 'zh-TW', 'zh-HK',
  'ko-KR', 'de-DE', 'fr-FR', 'es-ES', 'es-MX', 'pt-BR', 'pt-PT', 'it-IT', 'nl-NL', 'sv-SE', 'tr-TR', 'id-ID', 'vi-VN', 'th-TH'];
const CURRENCIES = ['USD', 'EUR', 'GBP', 'JPY', 'CNY', 'INR', 'KRW', 'BRL'];
const POWERS = [...Array(12).keys()].map(i => i + 3);
const NUMBER = /[0-9](?:[0-9.,  ' ]*[0-9])?/;

// The plural rule classes the engine implements, as functions of the displayed number's text (integer part i, visible
// fraction digits v). Each locale's class is the one that agrees with Intl.PluralRules on every probe.
const CLASSES = {
  none: () => 'other',
  'i1v0': (i, v) => (i === 1 && v === 0 ? 'one' : 'other'),
  'n1': (i, v, n) => (n === 1 ? 'one' : 'other'),
  'i01': (i) => (i === 0 || i === 1 ? 'one' : 'other'),
  'i0n1': (i, v, n) => (i === 0 || n === 1 ? 'one' : 'other'),
};
const PROBES = ['0', '0.5', '1', '1.0', '1.2', '1.5', '2', '5', '10', '11', '21', '100', '101'];
function classOf(locale) {
  const rules = new Intl.PluralRules(locale);
  for (const [name, f] of Object.entries(CLASSES)) {
    if (PROBES.every(t => {
      const [ip, fp = ''] = t.split('.');
      const opts = { minimumFractionDigits: fp.length, maximumFractionDigits: fp.length };
      return new Intl.PluralRules(locale, opts).select(Number(t)) === f(Number(ip), fp.length, Number(t));
    })) return name;
  }
  throw new Error(`no plural class fits ${locale}`);
}

function pattern(fmt, value) {
  const s = fmt.format(value);
  const m = s.match(NUMBER);
  if (!m) return { p: s, d: value };                              // spelled out: "mille"
  const shown = Number(m[0].replace(/[^0-9]/g, ''));
  return { p: s.slice(0, m.index) + '{n}' + s.slice(m.index + m[0].length), d: value / shown };
}

// For a compact formatter: per power, the "other" pattern from 5×10^p; the "one" pattern from 1.5×10^p shown to one
// place where that falls in the singular category (French "1,5 millier"), else from 10^p; and the exact form for a
// displayed 1, from 10^p itself (French and Italian spell it: "mille"). `fractional` makes the one-place formatter.
function compactPatterns(make, fractional, rule) {
  const fmt = make();
  return POWERS.map(p => {
    const other = pattern(fmt, 5 * 10 ** p);
    const atOne = pattern(fmt, 10 ** p);
    const exact = atOne.d === 10 ** p || !atOne.p.includes('{n}') ? atOne.p : other.p;
    let one = exact.includes('{n}') ? exact : other.p;
    if (rule === 'i01' || rule === 'i0n1') {
      const half = pattern(fractional(), 1.5 * 10 ** p);
      if (half.d === 10 ** p / 10) one = half.p;          // "1,5" reads as 15, so a 1.5×10^p shown as 1,5 divides by 10^p/10
    }
    return { exact, one, other: other.p, d: other.d };
  });
}

const out = {
  source: `Unicode CLDR ${process.versions.cldr} via ICU ${process.versions.icu} (Node ${process.version}), generated ${new Date().toISOString().slice(0, 10)}`,
  license: 'Derived from Unicode CLDR data. Copyright (c) 1991-2025 Unicode, Inc. Distributed under the Unicode License v3 (https://www.unicode.org/license.txt).',
  powers: POWERS,
  locales: {},
};
for (const l of LOCALES) {
  if (new Intl.NumberFormat(l).resolvedOptions().numberingSystem !== 'latn') throw new Error(`${l} does not use Latin digits`);
  const c = (o) => () => new Intl.NumberFormat(l, Object.assign({ notation: 'compact', maximumFractionDigits: 0 }, o));
  const f = (o) => () => new Intl.NumberFormat(l, Object.assign({ notation: 'compact', maximumFractionDigits: 1 }, o));
  const rule = classOf(l);
  const entry = {
    plural: rule,
    short: compactPatterns(c({ compactDisplay: 'short' }), f({ compactDisplay: 'short' }), rule),
    long: compactPatterns(c({ compactDisplay: 'long' }), f({ compactDisplay: 'long' }), rule),
    currency: {},
  };
  for (const cur of CURRENCIES) {
    const plain = new Intl.NumberFormat(l, { style: 'currency', currency: cur });
    const s = plain.format(1234.5), m = s.match(NUMBER);
    entry.currency[cur] = {
      plain: s.slice(0, m.index) + '{n}' + s.slice(m.index + m[0].length),
      digits: plain.resolvedOptions().maximumFractionDigits,
      compact: compactPatterns(c({ style: 'currency', currency: cur }), f({ style: 'currency', currency: cur }), rule),
      // A negative plain amount, where the minus sits is the locale's (Dutch: "US$ -4,00").
      negative: (() => { const n = plain.format(-1234.5); const k = n.match(NUMBER); return n.slice(0, k.index) + '{n}' + n.slice(k.index + k[0].length); })(),
      small: pattern(c({ style: 'currency', currency: cur })(), 5).p,
    };
  }
  const pct = new Intl.NumberFormat(l, { style: 'percent' }).format(0.5), pm = pct.match(NUMBER);
  entry.percent = pct.slice(0, pm.index) + '{n}' + pct.slice(pm.index + pm[0].length);
  const parts = new Intl.NumberFormat(l).formatToParts(12345678.5);
  entry.group = parts.find(x => x.type === 'group')?.value ?? '';
  entry.decimal = parts.find(x => x.type === 'decimal')?.value ?? '.';
  entry.minus = new Intl.NumberFormat(l).formatToParts(-1).find(x => x.type === 'minusSign')?.value ?? '-';
  // Grouping sizes, read off 123456789012: the last group and the repeating one ([3] or [3, 2]).
  const grouped = new Intl.NumberFormat(l, { useGrouping: 'always' }).format(123456789012).split(entry.group || '\u0000');
  entry.groups = grouped.length > 2 ? [grouped[grouped.length - 1].length, grouped[grouped.length - 2].length] : [3, 3];
  // Whether a four-digit number is grouped in plain notation (min-grouping 1) or not (2), as Spanish and Polish do.
  entry.minGroup = new Intl.NumberFormat(l).format(1234).includes(entry.group) ? 1 : 2;
  out.locales[l] = entry;
}

const VALUES = [999, 1000, 1234, 98765, 1234567, 12345678, 999950000, 130497000000, 72880000000, 3.6e11, -4250000000, 2000000, 1200000];
out.oracle = [];
for (const l of Object.keys(out.locales)) {
  for (const v of VALUES) {
    for (const compact of ['short', 'long'])
      out.oracle.push({ locale: l, value: v, options: { compact, decimals: 1 },
        text: new Intl.NumberFormat(l, { notation: 'compact', compactDisplay: compact, maximumFractionDigits: 1 }).format(v) });
    out.oracle.push({ locale: l, value: v, options: { compact: 'short', currency: 'USD', decimals: 1 },
      text: new Intl.NumberFormat(l, { notation: 'compact', style: 'currency', currency: 'USD', maximumFractionDigits: 1 }).format(v) });
    out.oracle.push({ locale: l, value: v, options: { compact: 'long', currency: 'USD', decimals: 1 },
      text: new Intl.NumberFormat(l, { notation: 'compact', compactDisplay: 'long', style: 'currency', currency: 'USD', maximumFractionDigits: 1 }).format(v) });
    out.oracle.push({ locale: l, value: v, options: { decimals: 1 }, text: new Intl.NumberFormat(l, { maximumFractionDigits: 1 }).format(v) });
    out.oracle.push({ locale: l, value: v, options: { currency: 'USD' }, text: new Intl.NumberFormat(l, { style: 'currency', currency: 'USD' }).format(v) });
  }
  for (const v of [0.462, -0.031, 0.5])
    out.oracle.push({ locale: l, value: v, options: { percent: true, decimals: 1 }, text: new Intl.NumberFormat(l, { style: 'percent', maximumFractionDigits: 1 }).format(v) });
}
const oracle = out.oracle; delete out.oracle;
fs.writeFileSync(process.argv[2], JSON.stringify(out, null, 1));
fs.writeFileSync(process.argv[3], JSON.stringify({ source: out.source, cases: oracle }, null, 1));
console.log(`${Object.keys(out.locales).length} locales, ${oracle.length} oracle cases`);
for (const [l, e] of Object.entries(out.locales)) process.stdout.write(`${l}:${e.plural}:${e.groups}:${e.minGroup} `);
console.log();
