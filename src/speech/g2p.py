"""Pronounces English for Motion.speech where a dictionary alone cannot: words it lacks, and heteronyms.

    python-speech/Scripts/python.exe src/speech/g2p.py models/nltk_data < request.json > answer.json

Two kinds of request. **A sentence**, `{"text": "I read it yesterday, and Zorblax reads it now."}`, answered one entry
per word in order:

    {"words": [{"word": "read", "phonemes": ["R", "EH1", "D"], "source": "homograph", "pos": "VBD"}, ...]}

`source` says where each came from: `dictionary` (the CMU Pronouncing Dictionary's first reading), `homograph` (a
heteronym, read by its part of speech in this sentence), or `model` (g2p-en's GRU, for a word no dictionary has).
Numbers are spelled out first ("$250" → "two hundred fifty dollars"), as g2p-en does.

**Just the gaps**, which is what Motion.speech sends when its own dictionary falls short: `{"words": ["zorblax"],
"numbers": ["4.5", "$250"]}`, answered keyed by what was sent, so nothing has to be matched up by position:

    {"words": {"zorblax": {"phonemes": ["Z", "AO1", ...], "source": "model"}}, "numbers": {"4.5": "four point five"}}

A word is guessed from its letters alone (anything else in it is dropped); a number comes back spelled out in words,
for the caller to pronounce as it pronounces any other. Measured on 2026-10-11, the guesses are plausible for invented
words and weaker for names; the homograph table is weak, which is why this mode does not use it.

**Offline by construction.** NLTK's data path is set to the folder given and nothing else, and `nltk.download` is
replaced with a refusal before g2p-en is imported, because g2p-en calls it at import when data is missing. The data
comes from tools/bootstrap.py, pinned and hashed, or not at all. g2p-en also checks for NLTK's old pickled tagger at
import; NLTK 3.9 and later never load it (`pos_tag` reads the JSON tagger), so that one check is answered with the
JSON tagger rather than fetching a pickle nothing will open.

The word-by-word loop below mirrors g2p-en 2.1.0's `G2p.__call__` (Kyubyong Park, Apache 2.0), keeping each word's
result and its source instead of flattening them. 2.1.0 is its last release, so its internals are fixed.
"""

from __future__ import annotations

import json
import re
import sys
import unicodedata


def load(data_dir: str):
    import nltk

    nltk.data.path[:] = [data_dir]

    def refuse(*_, **__):
        raise RuntimeError("nltk.download is disabled here: NLTK's data comes pinned from tools/bootstrap.py.")

    nltk.download = refuse
    real_find = nltk.data.find

    def find(resource, *args, **kwargs):
        if resource.rstrip("/") in ("taggers/averaged_perceptron_tagger", "taggers/averaged_perceptron_tagger.zip"):
            return real_find("taggers/averaged_perceptron_tagger_eng/", *args, **kwargs)
        return real_find(resource, *args, **kwargs)

    nltk.data.find = find
    try:
        from g2p_en import G2p
        from g2p_en.expand import normalize_numbers
    finally:
        nltk.data.find = real_find
    return G2p(), normalize_numbers


def pronounce(g2p, normalize_numbers, text: str) -> list[dict]:
    from nltk import pos_tag
    from nltk.tokenize import TweetTokenizer

    # g2p-en's own preprocessing: numbers spelled out, accents dropped, lower case, only letters and a few marks.
    text = normalize_numbers(str(text))
    text = "".join(c for c in unicodedata.normalize("NFD", text) if unicodedata.category(c) != "Mn")
    text = re.sub(r"[^ a-z'.,?!\-]", "", text.lower()).replace("i.e.", "that is").replace("e.g.", "for example")

    words = []
    for word, pos in pos_tag(TweetTokenizer().tokenize(text)):
        if not re.search("[a-z]", word):
            continue
        if word in g2p.homograph2features:
            pron1, pron2, pos1 = g2p.homograph2features[word]
            words.append({"word": word, "phonemes": pron1 if pos.startswith(pos1) else pron2, "source": "homograph", "pos": pos})
        elif word in g2p.cmu:
            words.append({"word": word, "phonemes": g2p.cmu[word][0], "source": "dictionary", "pos": pos})
        else:
            words.append({"word": word, "phonemes": g2p.predict(word), "source": "model", "pos": pos})
    return words


def gaps(g2p, normalize_numbers, words: list, numbers: list) -> dict:
    """Each word guessed (or found in NLTK's own CMUdict, should it have it), each number spelled out."""
    said = {}
    for word in words:
        letters = re.sub(r"[^a-z]", "", str(word).lower())
        if not letters:
            continue
        found = letters in g2p.cmu
        said[word] = {"phonemes": g2p.cmu[letters][0] if found else g2p.predict(letters),
                      "source": "dictionary" if found else "model"}
    spelled = {n: re.sub(r"\s+", " ", normalize_numbers(str(n))).strip() for n in numbers}
    return {"words": said, "numbers": spelled}


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: g2p.py <nltk_data folder>  (request JSON on stdin)", file=sys.stderr)
        return 2
    # Bytes, decoded allowing a byte-order mark: a Windows shell piping text in puts one at the front.
    request = json.loads(sys.stdin.buffer.read().decode("utf-8-sig"))
    g2p, normalize_numbers = load(sys.argv[1])
    if "text" in request:
        json.dump({"words": pronounce(g2p, normalize_numbers, request["text"])}, sys.stdout)
    else:
        json.dump(gaps(g2p, normalize_numbers, request.get("words", []), request.get("numbers", [])), sys.stdout)
    return 0


if __name__ == "__main__":
    sys.exit(main())
