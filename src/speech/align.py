"""Times a known line against a recording: when each word is said. For Motion.speech, whose words are known.

    python-speech/Scripts/python.exe src/speech/align.py models < request.json > answer.json

Request: `{"audio": "C:/.../line.wav", "words": ["WHO", "KNEW", "BOB", ...]}`: a 16-bit PCM WAV, any rate, mono or
stereo, and the words as spoken (numbers already spelled out), in order. Answer, one entry per word in that order:

    {"duration": 4.81, "heard": "WHO KNEW BOB ...", "words": [{"word": "WHO", "start": 0.26, "end": 0.48, "score": 0.93}, ...]}

`start` and `end` are seconds into the recording; `score` is the model's mean confidence over the word's letters, 0 to
1. A word with no letters the model knows comes back with `start` and `end` null.

`heard` is what the model hears with no line to hold it to: the likeliest letter in each frame, repeats and blanks
collapsed (greedy CTC decoding of the same emission, so it costs nothing more). Forced alignment cannot report a word
the line does not have; it folds it into a neighbour. anim3's take said "Shouting... Ahoy!", and the aligner gave
"Ahoy" 1.05 s at 0.71 while `heard` would have read SHOUTING A HOY. The caller compares the two.

**How.** wav2vec 2.0 base, fine-tuned on LibriSpeech's 960 hours to emit English letters (torchaudio's
WAV2VEC2_ASR_BASE_960H bundle; MIT), gives every 20 ms frame a probability for each letter, the apostrophe, the word
separator and a blank. torchaudio's CTC `forced_align` then finds the most probable path through exactly these words'
letters, separators between them, and each word spans its letters' frames. This is PyTorch's own forced-alignment
tutorial, which ForceAlign (MIT) also follows. Only words are timed here; the caller shares a word's span among its
phonemes, which a letters model cannot place.

**Offline by construction.** The weights are read from the folder given (tools/bootstrap.py puts them there, pinned
and hashed) with `weights_only`, and torch's downloader is replaced with a refusal first, so a missing file fails here
rather than fetching anything. The WAV is read with Python's `wave` module, not torchaudio's I/O, which moved to
TorchCodec and FFmpeg when torchaudio entered maintenance.
"""

from __future__ import annotations

import json
import sys
import wave

WEIGHTS = "wav2vec2_fairseq_base_ls960_asr_ls960.pth"


def load(models_dir: str):
    import torch
    import torch.hub
    import torchaudio

    def refuse(*_, **__):
        raise RuntimeError(f"Downloading is disabled here: {WEIGHTS} comes pinned from tools/bootstrap.py into {models_dir}.")

    torch.hub.download_url_to_file = refuse
    bundle = torchaudio.pipelines.WAV2VEC2_ASR_BASE_960H
    model = bundle.get_model(dl_kwargs={"model_dir": models_dir, "weights_only": True}).eval()
    return torch, torchaudio, bundle, model


def read_wav(path: str):
    """16-bit PCM samples as floats in -1..1, channels averaged, and the rate."""
    import numpy as np

    with wave.open(path, "rb") as w:
        if w.getsampwidth() != 2:
            raise ValueError(f"{path} is {8 * w.getsampwidth()}-bit; align.py reads 16-bit PCM WAV.")
        channels, rate, frames = w.getnchannels(), w.getframerate(), w.readframes(w.getnframes())
    samples = np.frombuffer(frames, dtype="<i2").astype(np.float32) / 32768.0
    if channels > 1:
        samples = samples.reshape(-1, channels).mean(axis=1)
    return samples, rate


def heard(emission, labels) -> str:
    """What the recording says by itself: the best label per frame, repeats and blanks collapsed, words upper-case."""
    out, prev = [], None
    for k in emission[0].argmax(-1).tolist():
        if k != prev and k != 0:
            out.append(labels[k])
        prev = k
    return " ".join("".join(out).replace("|", " ").split())


def align(torch, torchaudio, bundle, model, audio: str, words: list[str]) -> dict:
    samples, rate = read_wav(audio)
    waveform = torch.from_numpy(samples).unsqueeze(0)
    if rate != bundle.sample_rate:
        waveform = torchaudio.functional.resample(waveform, rate, bundle.sample_rate)
    duration = waveform.size(1) / bundle.sample_rate

    with torch.inference_mode():
        emission, _ = model(waveform)
        emission = torch.log_softmax(emission, dim=-1)

    # Each word's letters the model knows, separators between: "WHO|KNEW|BOB". A hyphen and anything else it has no
    # label for is dropped, so X-RAY is the letters XRAY.
    labels = bundle.get_labels()
    said = heard(emission, labels)
    index = {c: i for i, c in enumerate(labels)}
    letters = [[index[c] for c in w.upper() if c in index and c not in ("|", "-")] for w in words]
    targets, owner = [], []
    for i, ls in enumerate(letters):
        if not ls:
            continue
        if targets:
            targets.append(index["|"]); owner.append(None)
        targets.extend(ls); owner.extend([i] * len(ls))
    if not targets:
        return {"duration": duration, "heard": said,
                "words": [{"word": w, "start": None, "end": None, "score": None} for w in words]}

    path, scores = torchaudio.functional.forced_align(emission, torch.tensor([targets], dtype=torch.int32), blank=0)
    spans = torchaudio.functional.merge_tokens(path[0], scores[0].exp())
    if len(spans) != len(targets):
        raise RuntimeError(f"forced_align gave {len(spans)} spans for {len(targets)} letters; the recording may be too short for the line.")

    seconds = duration / emission.size(1)
    out = []
    for i, word in enumerate(words):
        mine = [s for s, o in zip(spans, owner) if o == i]
        if not mine:
            out.append({"word": word, "start": None, "end": None, "score": None})
            continue
        out.append({"word": word, "start": round(mine[0].start * seconds, 4), "end": round(mine[-1].end * seconds, 4),
                    "score": round(sum(s.score for s in mine) / len(mine), 4)})
    return {"duration": round(duration, 4), "heard": said, "words": out}


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: align.py <models folder>  (request JSON on stdin)", file=sys.stderr)
        return 2
    # Bytes, decoded allowing a byte-order mark: a Windows shell piping text in puts one at the front.
    request = json.loads(sys.stdin.buffer.read().decode("utf-8-sig"))
    torch, torchaudio, bundle, model = load(sys.argv[1])
    json.dump(align(torch, torchaudio, bundle, model, request["audio"], list(request["words"])), sys.stdout)
    return 0


if __name__ == "__main__":
    sys.exit(main())
