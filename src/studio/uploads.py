"""Files a visitor attaches to a request, on their way into the project's `documents/`.

The same rules the ADK intake applies to its one document (`src/adk_agent/intake.py`), restated here
because the studio cannot import the ADK runtime: a sanitised name, a type `Documents.ask` can declare,
a byte cap counted as the bytes arrive, and a cap on how many. `documents/` is never served by any
route (`app.PRIVATE_FOLDERS`), so a file staged here reaches the agent and nobody else.
"""

from __future__ import annotations

import re
from pathlib import Path

from fastapi import UploadFile

from .runs import StudioError

#: Mirrors `Documents.MimeTypes` on the .NET side, as `newproject.DOCUMENT_SUFFIXES` does: a file
#: outside this set would sit in `documents/` where the agent can see it and cannot read it.
DOCUMENT_SUFFIXES = frozenset({
    ".pdf", ".txt", ".md", ".csv", ".json", ".xml", ".html", ".htm",
    ".png", ".jpg", ".jpeg", ".webp",
})

#: Per file. Below `Documents.MaxInlineBytes` (18 MB), so nothing accepted is too large to read.
MAX_UPLOAD_BYTES = 16 * 1024 * 1024

#: Per request. A brief with more references than this is a brief that has not chosen.
MAX_FILES = 8

CHUNK = 64 * 1024


def safe_filename(raw: str | None) -> str:
    """The name to write, with every route out of the directory removed. Raises `StudioError`.

    `UploadFile.filename` is a multipart header the client wrote. The basename of both separator
    conventions, trailing dots and spaces stripped (Windows drops them after any literal check), and
    then a conservative allowlist: nothing left can traverse or name a stream.
    """
    name = (raw or "").replace("\\", "/").split("/")[-1].strip().rstrip(". ")
    if not name or set(name) <= {"."}:
        raise StudioError("One of the files has no usable name.")
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._ -]{0,127}", name):
        raise StudioError(f"{name!r} is not a usable file name. Use letters, digits, spaces, dots, "
                          "dashes and underscores.")
    if Path(name).suffix.lower() not in DOCUMENT_SUFFIXES:
        raise StudioError(f"{name} is not a type the studio can read. Use one of: "
                          + ", ".join(sorted(DOCUMENT_SUFFIXES)))
    return name


async def stage(files: list[UploadFile], into: Path) -> list[Path]:
    """Streams every attached file into `into`, refusing the whole set on the first bad one."""
    files = [f for f in files if f is not None and (f.filename or "").strip()]
    if len(files) > MAX_FILES:
        raise StudioError(f"That is {len(files)} files; a request takes up to {MAX_FILES}.")

    staged: list[Path] = []
    for upload in files:
        name = safe_filename(upload.filename)
        target = into / name
        if target.exists():
            raise StudioError(f"Two files are called {name}; rename one of them.")

        written = 0
        with target.open("wb") as out:
            while chunk := await upload.read(CHUNK):
                written += len(chunk)
                if written > MAX_UPLOAD_BYTES:
                    out.close()
                    target.unlink(missing_ok=True)
                    raise StudioError(f"{name} is larger than the {MAX_UPLOAD_BYTES // (1024 * 1024)} MB limit.")
                out.write(chunk)

        if written == 0:
            target.unlink(missing_ok=True)
            raise StudioError(f"{name} is empty.")
        staged.append(target)

    return staged
