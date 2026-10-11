# Speech data

`Motion.speech(text)` turns a line of dialogue into phonemes and mouth shapes. Two things it uses come from elsewhere.

## `cmudict.dict` — the CMU Pronouncing Dictionary

**Fetched, not committed.** `tools/bootstrap.py` downloads `cmudict.dict` and its `LICENSE` (saved here as
`cmudict.LICENSE`) from CMU Sphinx's repository, [cmusphinx/cmudict](https://github.com/cmusphinx/cmudict), at a pinned
commit, and refuses them unless their SHA-256 matches. The build copies both next to `Polson.Animation.dll`. Both
files are gitignored.

```
py tools/bootstrap.py --only speech
```

126,052 words in 135,166 lines, one pronunciation per line: `word PH ON EH1 M Z` in lower case, `word(2)` for a further pronunciation, and on a
few lines a note after `#`. `Motion.speech` takes each word's first pronunciation and drops the notes.

Its licence is BSD-style, Carnegie Mellon University's, and it travels with the dictionary as `cmudict.LICENSE`:

> Copyright (C) 1993-2015 Carnegie Mellon University. All rights reserved.
>
> Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
> following conditions are met: (1) redistributions of source code must retain the above copyright notice, this list of
> conditions and the following disclaimer — the contents of the file are deemed to be source code; (2) redistributions
> in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the
> documentation and/or other materials provided with the distribution.

The full text, with its acknowledgements and disclaimer, is `cmudict.LICENSE`.

## The mouth tables — Papagayo-NG

The phoneme-to-mouth tables in `MotionSpeech.cs` (`MouthSets`: Preston Blair's ten, Fleming & Dobbs's eleven, Rhubarb's
nine) are Papagayo-NG's `phonemes/preston_blair.json`, `fleming_dobbs.json` and `rhubarb.json`, from the Morevna
Project's Papagayo-NG after Mike Clifton's Papagayo (2005), under the GNU GPL version 2 or (at your option) any later
version.

Papagayo-NG's mouth images are **not** used or included: they carry no licence.

## What is ours

The timing: every duration, the phrase-end lengthening, the pauses, the lead and the crossfade are studio rules, not
measurements. Nothing in `reference/` measures speech timing.
