"""What a visitor can commission, in their words rather than the studio's.

The developer page offers `projects.WORKFLOWS` as it is: template names, types, and a brief box. A
person commissioning work does not think in templates. They want a film trailer or a poster, in some
sizes and some formats, made from some files — so this is the mapping from that request to the
workflow, type and brief the studio actually runs.

**Every choice here ends as text in `brief.md` or as an argument `projects.check` already validates.**
Nothing in this module selects a template by a visitor's string: a card names its workflows, a route
picks one of them by index into that closed list, and the type is checked against
`projects.WORKFLOWS` by the code that spawns the CLI. Sizes and formats are *asked for* in the brief,
not enforced — the workflows read the brief as prose, so a size the agent ignores is a size the
agent ignores, and the page must not promise more than that.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field

from . import projects
from .runs import StudioError

#: A request's title is a sentence a person typed; its project id is derived from it.
MAX_TITLE = 80

#: A free-text extra (a headline, an author) is a line, not a brief.
MAX_EXTRA = 200

#: A long extra (an audience's questions) is a paragraph, drawn as a box under the brief; still far short of a brief.
MAX_LONG_EXTRA = 2000

#: Largest custom dimension, in pixels. Past this the engine's own canvas caps would refuse it later.
MAX_SIDE = 8192

#: Qloo's age bands, as `Taste` takes them (`QlooParameters` on the .NET side), with the words a person reads.
AGE_BANDS: tuple[tuple[str, str], ...] = (
    ("24_and_younger", "24 and younger"), ("25_to_29", "25 to 29"), ("30_to_34", "30 to 34"),
    ("35_to_44", "35 to 44"), ("45_to_54", "45 to 54"), ("55_and_older", "55 and older"))

#: The poster and trailer briefs name audiences A, B…. Six lets a set span regions (a western, an African, a Middle
#: Eastern and an Asian audience, say); the run's time, tokens and Qloo budget grow with the count (`intake`).
MAX_AUDIENCES = 6

#: What the composer's place fields suggest, one region each, in the order the rows appear.
EXAMPLE_PLACES = ("Tokyo", "Lagos", "Dubai", "Berlin", "São Paulo", "Mumbai")

#: A place name as a person types it: letters in any script, spaces and a little punctuation. `Taste` resolves it.
PLACE = re.compile(r"^[^\W\d_][^\W_]*(?:[ .,'’()-]+[^\W_]+)*\.?$")


@dataclass(frozen=True)
class Size:
    label: str
    width: int
    height: int

    @property
    def key(self) -> str:
        return f"{self.width}x{self.height}"

    @property
    def detail(self) -> str:
        return f"{self.width} × {self.height} px"


@dataclass(frozen=True)
class Format:
    ext: str
    label: str


@dataclass(frozen=True)
class Route:
    """One workflow a card can run, as the visitor would choose between them."""

    workflow: str
    label: str
    detail: str = ""


@dataclass(frozen=True)
class Extra:
    """A workflow-specific field. `options` empty means free text."""

    key: str
    label: str
    options: tuple[str, ...] = ()
    default: str = ""
    placeholder: str = ""
    #: A paragraph rather than a line: drawn as a text box under the brief, line breaks kept.
    long: bool = False


@dataclass(frozen=True)
class Card:
    slug: str
    title: str
    kind: str                       # video | motion | image | document — sets the icon, sizes and formats
    meta: str
    routes: tuple[Route, ...]
    image: str = ""                 # under static/gallery/, the studio's own work in this workflow
    alt: str = ""
    extras: tuple[Extra, ...] = ()
    style_label: str = "Style"      # what the workflow's types are called on this card
    type_labels: dict[str, str] = field(default_factory=dict)
    audiences: bool = False         # asks who it is for: a place and an age band per audience, A, B…
    #: Audiences may be left out: a report explainer for "shareholders" has no place to measure, one for Mumbai and Tokyo
    #: does, and gets a film per audience in its conventions (pace from Qloo, numbers as the place writes them).
    audiences_optional: bool = False
    #: The workflow types this card offers, when not all of them: `()` for none, one for a card that *is* that type.
    #: A film trailer and a product ad are one workflow; without this each card would offer the other's type, and a
    #: lone type is chosen for the visitor (`front.commissioned`), so every film trailer would become an ad.
    types: tuple[str, ...] | None = None
    #: A file the request cannot run without, by extension: a report explainer with no report has nothing to explain.
    needs: str = ""

    @property
    def multi_route(self) -> bool:
        return len(self.routes) > 1

    @property
    def fixed_type(self) -> str:
        """The type this card always runs, or ''. Not offered as a choice: the card is the choice."""
        return self.types[0] if self.types is not None and len(self.types) == 1 else ""

    def check_files(self, names: list[str]) -> None:
        """Refuses a request missing the file this card needs. Raises `StudioError`."""
        if self.needs and not any(n.lower().endswith(self.needs) for n in names):
            raise StudioError(f"{self.title} needs its document: attach the {self.needs[1:].upper()}.")

    @property
    def piece(self) -> str:
        """What one audience gets, in the visitor's words."""
        return "ad" if self.fixed_type == "product" else "film" if self.kind == "video" else "piece"


#: Presets per kind. A tile draws the ratio, so the order is the order a person scans in.
SIZES: dict[str, tuple[Size, ...]] = {
    "video": (Size("Landscape", 1920, 1080), Size("Story", 1080, 1920), Size("Square", 1080, 1080),
              Size("Portrait", 1080, 1350)),
    "motion": (Size("Square", 1080, 1080), Size("Landscape", 1920, 1080), Size("Story", 1080, 1920),
               Size("Portrait", 1080, 1350)),
    "image": (Size("Landscape", 1920, 1080), Size("Portrait", 1080, 1350), Size("Story", 1080, 1920),
              Size("Square", 1080, 1080), Size("Poster", 1200, 1800), Size("A4 print", 2480, 3508),
              Size("Banner", 1500, 500), Size("Cinema", 2048, 858)),
    "document": (Size("A4 print", 2480, 3508), Size("Landscape", 1920, 1080), Size("Portrait", 1080, 1350),
                 Size("Square", 1080, 1080), Size("Tall", 1080, 2700)),
}

#: What each kind can be delivered as. Only formats the engine writes: outFile, outSvg, outPdf, saveVideo, .sif.
FORMATS: dict[str, tuple[Format, ...]] = {
    # The trailer workflow writes the film and, when silent, a looping .webp of it. No .sif: the template
    # forbids writing one during a run, and it is exported afterwards with `polson eval --sif`.
    "video": (Format(".mp4", "Video"), Format(".webp", "Looping preview")),
    "motion": (Format(".webp", "Animation"), Format(".mp4", "Video"), Format(".sif", "Synfig file")),
    "image": (Format(".png", "Image"), Format(".webp", "Web image"), Format(".svg", "Vector"),
              Format(".pdf", "Print")),
    "document": (Format(".svg", "Vector"), Format(".png", "Image"), Format(".pdf", "Print")),
}

CARDS: tuple[Card, ...] = (
    # The workflow is built and measured around 15-second films; longer ones are more encode parts and more
    # clock, so 30 is the most the form offers. A director who wants longer says so in the brief.
    Card("film-trailer", "Film trailer", "video", "Video · 15 seconds, one film per audience",
         (Route("trailer", "Motion graphics trailer"),),
         extras=(Extra("duration", "Duration", ("15 seconds", "30 seconds"), "15 seconds"),),
         audiences=True, types=()),
    # Dynamic creative: one product, an ad per audience, each from that audience's buyers (Qloo). The trailer
    # workflow's `product` type; its evidence method is `docs/internal/product-ad-probe.md`.
    Card("product-ad", "Product ad", "video", "Video · 15 seconds, one ad per audience",
         (Route("trailer", "Motion graphics ad"),),
         extras=(Extra("product", "Product", placeholder="Its name, and what it is"),
                 Extra("comparables", "Comparable brands", placeholder="Two or three real brands like it"),
                 Extra("claim", "Claim", placeholder="The one claim, worded as it may appear"),
                 Extra("cta", "Call to action", placeholder="What the viewer should do next"),
                 Extra("duration", "Duration", ("15 seconds", "30 seconds"), "15 seconds")),
         audiences=True, types=("product",)),
    # An audience's questions answered from a long document the director attaches — an annual report, a 10-K. The
    # trailer workflow's `report` type: Nemotron reads the PDF as page text through `Documents.ask`, and every figure
    # on screen is one the engine found on its page. Audiences are optional: with places, a film for each, paced from
    # its Qloo profile and with its figures written as that place writes them (Locale.number); without, one film.
    Card("report-explainer", "Report explainer", "video", "Video · 45 seconds, questions answered from a report",
         (Route("trailer", "Motion graphics explainer"),),
         extras=(Extra("viewer", "Who is watching", placeholder="Who they are, and what they already know", long=True),
                 Extra("questions", "Their questions", placeholder="One question a line, up to five", long=True),
                 Extra("duration", "Duration", ("45 seconds", "60 seconds"), "45 seconds")),
         audiences=True, audiences_optional=True, types=("report",), needs=".pdf"),
    # A startup's case told to investors from its own pitch deck, and a financial model or metrics where it has them. The
    # trailer workflow's `pitch` type: the documents read and checked as a report's are, every projection drawn and
    # labelled as one, the ask as stated. With places, a film per investor audience in its conventions; without, one.
    Card("startup-pitch", "Startup pitch", "video", "Video · 2 minutes from the deck, one film per investor audience",
         (Route("trailer", "Motion graphics pitch"),),
         extras=(Extra("company", "Company", placeholder="Its name, as the deck gives it"),
                 Extra("told_as", "Told as", ("The company's own pitch", "A retelling of a past deck"),
                       "The company's own pitch"),
                 Extra("stage", "Stage", ("As the deck says", "Pre-seed", "Seed", "Series A", "Series B or later"),
                       "As the deck says"),
                 Extra("raise", "The raise", placeholder="Amount and instrument, as the company states it; blank for the deck's words"),
                 Extra("use_of_funds", "Use of funds", placeholder="What the money is for, if the deck does not say", long=True),
                 Extra("duration", "Duration", ("90 seconds", "2 minutes", "3 minutes"), "2 minutes"),
                 Extra("narration", "Narration", ("A voice", "None: every word on screen"), "A voice")),
         audiences=True, audiences_optional=True, types=("pitch",), needs=".pdf"),
    # A theorem or concept explained in a film: equations, graphs and narration, every formula and claim checked before
    # it is drawn and the film read back after. The Nebius entry's; the `math_explainer` workflow.
    Card("maths-explainer", "Maths explainer", "video", "Video · a theorem or concept, every step checked",
         (Route("math_explainer", "Maths explainer"),),
         extras=(Extra("topic", "Theorem or concept", placeholder="Named as a textbook would name it"),
                 Extra("viewer", "Who is watching", placeholder="Their level, and what they already know", long=True),
                 Extra("duration", "Duration", ("45 seconds", "60 seconds", "90 seconds"), "60 seconds"),
                 Extra("narration", "Narration", ("A voice", "None: every word on screen"), "A voice"))),
    Card("motion-graphic", "Motion graphic", "motion", "Animation · one action, keyed",
         (Route("animation", "One keyed action"),),
         extras=(Extra("duration", "Duration", ("2 seconds", "4 seconds", "8 seconds"), "4 seconds"),
                 Extra("loop", "Loop", ("Loops", "Plays once"), "Loops"))),
    Card("storyboard", "Storyboard", "image", "Image · 6 to 12 panels",
         (Route("storyboard_cast", "Built cast", "The same people in every panel, able to turn round"),
          Route("storyboard", "Bought cast", "One picture per expression; quicker when no one turns"),
          Route("storyboard_quick", "Quick board", "Fewer panels on a short clock")),
         image="storyboard.webp", alt="A six-panel storm-night storyboard",
         extras=(Extra("panels", "Panels", ("4 panels", "6 panels", "8 panels", "12 panels"), "6 panels"),)),
    Card("comic", "Comic", "image", "Image · a page of panels",
         (Route("comic", "Comic page"), Route("comic_studio", "Comic with a studio of agents", "Several agents, one page")),
         image="comic.webp", alt="An inked gull riding a bicycle on a boardwalk",
         style_label="How to work", type_labels={"review": "Work with me", "seed": "Surprise me"}),
    Card("poster", "Poster", "image", "Image · grounded in audience taste",
         (Route("poster", "Taste-grounded poster"),),
         image="poster.webp", alt="A neo-brutalist poster on the history of the web",
         style_label="Kind", type_labels={"film": "Film poster", "product": "Product ad"},
         extras=(Extra("headline", "Headline", placeholder="The words that must appear"),),
         audiences=True),
    Card("book-cover", "Book cover", "image", "Image · one composed cover",
         (Route("cover", "Composed cover"),),
         extras=(Extra("book_title", "Book title", placeholder="As it should appear"),
                 Extra("author", "Author", placeholder="As it should appear"))),
    Card("logo", "Logo and identity", "image", "Image · a mark and its sheet",
         (Route("logo", "Mark and brand sheet"),),
         image="logo.webp", alt="A brand presentation sheet for a P mark",
         type_labels={"antique": "Antique", "geometric": "Geometric", "modern": "Modern"},
         extras=(Extra("name", "Name", placeholder="The name the mark is for"),)),
    Card("infographic", "Infographic", "document", "Document · checked figures",
         (Route("vector_infographic", "Vector", "Delivered as SVG, every figure checked"),
          Route("infographic", "Raster", "Delivered as an image")),
         image="infographic.webp", alt="A blueprint infographic of a lunar descent",
         type_labels={t: t.capitalize() for t in projects.WORKFLOWS["infographic"]}),
    Card("painting", "Painting", "image", "Image · one scene",
         (Route("painting", "Painted scene"),),
         image="painting.webp", alt="A lit house behind bare trees at dusk"),
    Card("drawing", "Drawing", "image", "Image · pencil to finish",
         (Route("drawing", "Drawing", "Built up in passes"),
          Route("sketch_scene", "Sketched scene", "One scene from gesture")),
         image="drawing.webp", alt="A pencil drawing of an arcade in perspective",
         style_label="How to work", type_labels={"review": "Work with me", "seed": "Surprise me"}),
)

BY_SLUG: dict[str, Card] = {c.slug: c for c in CARDS}

#: Which card a project belongs to, for projects made on the developer page or by another host.
#: The first card wins: `trailer` is the film trailer's before it is the product ad's. A project's own record names its
#: card, so this is only the fallback for one made elsewhere.
BY_WORKFLOW: dict[str, Card] = {}
for _card in CARDS:
    for _route in _card.routes:
        BY_WORKFLOW.setdefault(_route.workflow, _card)


def card_for(workflow: str) -> Card | None:
    return BY_WORKFLOW.get(workflow)


def types_for(card: Card, workflow: str) -> list[tuple[str, str]]:
    """The styles offered for this route, as (value, label), from the closed set in `projects` narrowed by the card."""
    return [(t, card.type_labels.get(t, t.capitalize())) for t in projects.WORKFLOWS.get(workflow, ())
            if card.types is None or t in card.types]


def slugify(title: str) -> str:
    """A project id from a title: what `projects.VALID_ID` accepts, readable, never empty."""
    slug = re.sub(r"[^a-z0-9]+", "-", title.lower()).strip("-")[:48].strip("-")
    return slug or "request"


@dataclass
class Commission:
    """A visitor's request, checked, ready to become a project."""

    card: Card
    title: str
    workflow: str
    kind: str
    sizes: list[Size]
    formats: list[Format]
    extras: list[tuple[str, str]]
    brief: str
    audiences: list[tuple[str, str]] = field(default_factory=list)   # (place, age band code or "")

    def audience_lines(self) -> list[str]:
        """'Audience A: Tokyo, aged 45 to 54', as the poster and trailer briefs name them."""
        words = dict(AGE_BANDS)
        return [f"Audience {chr(65 + i)}: {place}" + (f", aged {words[age]}" if age else "")
                for i, (place, age) in enumerate(self.audiences)]

    def brief_text(self, files: list[str]) -> str:
        """The brief as the agent will read it: the visitor's words, then what to deliver.

        The delivery block is plain Markdown after a rule, so it reads as part of the commission and
        survives the sanitiser untouched. It asks; nothing downstream enforces it.
        """
        lines = [self.brief.strip(), "", "---", "", "## What to deliver", "",
                 f"- Request: {self.title}",
                 f"- Workflow: {self.card.title}"]
        lines += [f"- {line}" for line in self.audience_lines()]
        if len(self.audiences) > 1:
            lines.append(f"- One {'ad' if self.kind == 'product' else self.card.piece} per audience")
        lines += ["- Sizes: " + "; ".join(f"{s.label} {s.detail}" for s in self.sizes),
                  "- Formats: " + ", ".join(f.ext for f in self.formats)]
        for label, value in self.extras:
            asked = [q.strip() for q in re.split(r"[;\n]", value) if q.strip()] if label == "Their questions" else []
            lines += [f"- {label}:"] + [f"  {i}. {q}" for i, q in enumerate(asked, 1)] if asked else [f"- {label}: {' '.join(value.split())}"]
        clips = [f for f in files if f.lower().endswith(".mp4")]
        others = [f for f in files if f not in clips]
        if others:
            lines.append("- Files supplied, in documents/: " + ", ".join(others))
        if clips:
            lines.append("- Reference video, in documents/ (watch with Video.ask): " + ", ".join(clips))
        return "\n".join(lines) + "\n"

    def record(self) -> dict:
        """What the request page shows about the request, written beside the project as request.json."""
        return {"title": self.title, "card": self.card.slug, "workflow": self.workflow, "type": self.kind,
                "sizes": [{"label": s.label, "width": s.width, "height": s.height} for s in self.sizes],
                "formats": [f.ext for f in self.formats],
                "extras": [{"label": k, "value": v} for k, v in self.extras],
                "audiences": [{"letter": chr(65 + i), "place": p, "age": a, "ageLabel": dict(AGE_BANDS).get(a, "")}
                              for i, (p, a) in enumerate(self.audiences)]}


def parse_audiences(places: list[str], ages: list[str]) -> list[tuple[str, str]]:
    """Pairs of (place, age band) from the form's parallel lists, blank rows dropped. Raises `StudioError`."""
    bands = {code for code, _ in AGE_BANDS}
    found = []
    for i, place in enumerate(places):
        place = " ".join((place or "").split())
        age = (ages[i] if i < len(ages) else "") or ""
        if not place:
            if age:
                raise StudioError("Each audience needs a place, such as a city or a country.")
            continue
        if len(place) > 60 or not PLACE.match(place):
            raise StudioError(f"{place[:60]!r} does not read as a place name. Use a city or country, such as Mumbai.")
        if age and age not in bands:
            raise StudioError("Choose an age band from the list.")
        found.append((place, age))
    if len(found) > MAX_AUDIENCES:
        raise StudioError(f"That is {len(found)} audiences; a request takes up to {MAX_AUDIENCES}.")
    return found


def parse(card_slug: str, title: str, route: str, kind: str, sizes: list[str], custom: str,
          formats: list[str], extras: dict[str, str], brief: str,
          places: list[str] | None = None, ages: list[str] | None = None) -> Commission:
    """Checks a composed request against the card's closed sets. Raises `StudioError` with a reason."""
    card = BY_SLUG.get(card_slug)
    if card is None:
        raise StudioError("That workflow is not one the studio offers.")

    title = " ".join((title or "").split())
    if not title:
        raise StudioError("Give the request a name, so you can find it again.")
    if len(title) > MAX_TITLE:
        raise StudioError(f"That name is {len(title)} characters; keep it under {MAX_TITLE}.")

    try:
        chosen = card.routes[int(route or 0)]
    except (ValueError, IndexError):
        raise StudioError("Choose one of the offered routes.") from None

    offered = {t for t, _ in types_for(card, chosen.workflow)}
    if kind and kind not in offered:
        raise StudioError(f"{card.title} does not offer that {card.style_label.lower()}.")
    kind = card.fixed_type or kind

    presets = {s.key: s for s in SIZES[card.kind]}
    picked = [presets[k] for k in dict.fromkeys(sizes) if k in presets]
    if len(picked) != len(set(sizes)):
        raise StudioError("One of the sizes is not a preset; use the custom size for anything else.")
    if custom.strip():
        m = re.fullmatch(r"\s*(\d{2,5})\s*[x×]\s*(\d{2,5})\s*", custom)
        if not m or not all(16 <= int(v) <= MAX_SIDE for v in m.groups()):
            raise StudioError(f"A custom size is width × height in pixels, each between 16 and {MAX_SIDE}.")
        picked.append(Size("Custom", int(m.group(1)), int(m.group(2))))
    if not picked:
        raise StudioError("Choose at least one size.")

    allowed = {f.ext: f for f in FORMATS[card.kind]}
    chosen_formats = [allowed[f] for f in dict.fromkeys(formats) if f in allowed]
    if len(chosen_formats) != len(set(formats)):
        raise StudioError(f"{card.title} cannot be delivered in one of those formats.")
    if not chosen_formats:
        raise StudioError("Choose at least one format.")

    pairs = []
    for extra in card.extras:
        raw = extras.get(extra.key) or extra.default
        # A long field keeps its lines (one question a line); each line, and a short field, is one run of words.
        value = ("\n".join(" ".join(line.split()) for line in raw.splitlines() if line.strip()) if extra.long
                 else " ".join(raw.split()))
        limit = MAX_LONG_EXTRA if extra.long else MAX_EXTRA
        if extra.options and value not in extra.options:
            raise StudioError(f"Choose a {extra.label.lower()} from the list.")
        if len(value) > limit:
            raise StudioError(f"{extra.label} is {len(value)} characters; keep it under {limit}.")
        if value:
            pairs.append((extra.label, value))

    audiences = parse_audiences(places or [], ages or []) if card.audiences else []
    if card.audiences and not audiences and not card.audiences_optional:
        raise StudioError(f"Say who the {card.title.lower()} is for: a place and, if it matters, an age band.")

    if not (brief or "").strip():
        raise StudioError("A brief is the one thing the studio cannot infer. Say what you want made.")

    return Commission(card, title, chosen.workflow, kind, picked, chosen_formats, pairs, brief, audiences)
