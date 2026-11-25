## What it does
Polson Graphics Studio is an agentic co-creative graphics and motion design platform designed to provide an autonomous, affordable alternative to human-powered on-demand creative service platforms like [Design Pickle](https://designpickle.com/) and [Superside](https://www.superside.com/), with innovative and accurate graphic and motion design work that adapts to different audiences and languages and cultures all over the world. Polson Graphics Studio provides a verifiable, culturally adaptable, alternative to diffusion model-powered generative AI systems like Google Veo or Adobe Firefly, for professional-grade image and motion graphic design work for infographics, product advertisements, film trailers, logos, posters, and many other types of graphic and motion design projects where precision, accuracy, and cross-cultural adaptation is crucial. 

![](https://i.imgur.com/isBlYIm.png)

![](https://imgur.com/jqDfKc7.png)


![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/trailer5.webp)

![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/output.webp)

Polson's overall goal is to allow ordinary users to harness their creativity using AI with professional-level graphic and motion design that would be far too expensive to employ via traditional means, mirroring the use of AI for software development by non-expert users today, and which can't be done by current generative AI systems. It aims to be superior to even the most advanced diffusion model-powered image and video generators in the areas of precision and accuracy and cross-cultural adaptation. 
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/allin_final_a.svg)

In [contrast](https://higgsfield.ai/blog/AI-Motion-Design-When-Video-Generation-Meets-Code) to standard AI image or video generators that use diffusion models, Polson Graphics Studio uses a  procedural drawing and animation engine that agents using multimodal LLMs like Claude Opus and Google Gemini write code for to create visual artifacts that they can perceive both visually and as code.  Polson attempts to bring the same co-creative, verifiable environment for agentic software development that modern artificial intelligence enables, to the task of visual art and graphic design.  The agent has access to a wide range of tools to interact with, experiment on, and verify the drawing and animations it creates, to access and use knowledge in graphic design manuals and other resources, and to offload research tasks to cloud-based systems like [Parallel.ai](https://parallel.ai/).  

Each studio project collects natural language briefs, PDF documents, brand images and other user-supplied assets and follows a formal graphic design workflow, with detailed validation phases designed to check the accuracy of each fact or number  that appears on-screen and delivers raster and vector images and videos in different formats together with creative briefs, brand sheets, mood boards, verification reports, and many different types of artifacts that are typically the result of the graphic design process.

![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/kitofparts2.png)

## Polson Framework
[Polson](https://github.com/allisterb/Polson) is a framework for agentic co-creative visual art and graphic design, built on the principles of [Enactive Co-Creative AI](https://www.co-creativeai.com/). Polson attempts to address the fundamental limitations of diffusion model-based image and video generators for graphic design work with a code-based procedural drawing and animation engine that agents using multimodal LLMs like Gemini and Claude write code for to create visual artifacts that they can perceive both visually and as code, and can make precise, verifiable, mutations of the visual artifact state through code operations in response to instructions or user or other agent feedback, without drifting and losing coherency. Polson attempts to bring the same co-creative environment for software development collaboration and automation to the task of visual art and graphic design.  

In the field of computational creativity, co-creative agents collaborate with humans continuously in real time with improvisations that enrich the whole creative process. 
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/interaction1.png)
Co-creativity allows participants to improvise and fuse and construct ideas based on decisions of their peers so the whole creative product emerges through interaction and negotiation between  multiple peers and is greater than the sum of individual parts.
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/interaction2.png)

### Graphic Design in Polson
Polson Graphics Studio uses a procedural drawing and animation engine combined with a formal graphic and motion design process and a rigorous verification
phase powered by a VLM model and research services like [Parallel.ai](https://docs.parallel.ai/decisions/decisions-quickstart) that verify as much as possible every fact and figure that appears in a graphic. Agents do not rely on diffusion models to generate images or graphics which always carries the probability of hallucinations or inconsistency. Instead the agent writes code to create and assemble the parts of the graphic production, within the framework of a graphic design process and workflow and verification process the agent is instructed to follow. This makes Polson suitable for autonomously designing things like VC pitch decks and financial report visualization where accuracy and verisimilitude are critical.

The graphic design process Polson agents follow is based on books like Ellen Lupton's *Graphic Design Thinking: Beyond Brainstorming*, with steps and processes like: 
* Brainstorming
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/braindump2.png)
* Mind-mapping
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/mindmap.png)
* Brand Matrix
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/brandmatrix.png)
* Visual Research
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/visual_research.png)
* Rhetorical Figures
* Alternative Grids
* Kit of Parts
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/motionkitofparts.png)

Motion design uses artifacts like design boards:
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/designboard.png)


### Verification in Polson
Verification in Polson has 2 parts: design verification and data verification.

#### Design Verification
An integral part of the graphics design process in Polson are checks the agent can do against documented graphic and motion design guidelines and constraints, as well as verification of the figures and facts that appear on screen
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/checks2.png)
Checks can be done via code or visually by the agent looking at an image or video and judging for itself:
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/verify-jap.png)

Layout is measured, not eyeballed, because models are poor at noticing their own layout mistakes. Every image Polson renders is measured automatically, and the agent is told about any line of text that:
- sits on top of another line,
- runs off the edge of the page,
- has a rule or divider running through it,
- runs past the side of the box or card it sits on, or into the element beside it,
- or is set in a typeface that isn't available and was silently replaced.

Films get their own checks: every line held on screen long enough to read, nothing outside the title-safe area, no two lines overlapping once they settle, figures that arrive whole rather than counting up (a counter shows numbers the document never printed), and text contrast of at least 4.5 : 1.

### Data verification
Diffusion models can draw charts and infographics but they cannot tell you where the numbers on it came from, and they will happily draw figures that are nowhere in the sources. For visualizations of financial reports and investor pitches and other documents, that is the one thing that can't happen. 

In Polson every piece goes through data verification checks the agent cannot skip, and the **Verification** tab lists each check, including the ones that failed and how they were fixed.
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/verification.png)

When a piece is finished, the studio lists every text on it and looks up where each one came from:
- **A document**: the figure is found on a page of the PDF the client supplied, by searching the page's text, not by trusting the model. For long filings like an S-1, the agent first finds the right pages by their headings, then asks each question of those pages only, and every figure in the answer is checked against the page it came from.
- **The brief**, or **research** through Parallel.ai, which returns a citation for every field.
- **Arithmetic**: a growth rate or a share is computed in the script, and the working is passed along with it ("16,286 / 1,163 = 14.003").
- **Facts no document holds**, like a film's credits or "no later filing has set a price range", are checked on the web through Parallel.ai, and come back supported, unsettled or not supported.

A figure with no source fails the check and has to come off the screen. Figures from outside a filing are held to a higher bar: they must come from a primary source (a regulator's filing, an exchange, the company itself), and their digits must appear in the excerpt that source returned. 

The 2nd phase of validation is where an independent model reads the finished piece. After the film is encoded or the image rendered, a separate model watches or reads it the way a viewer would, and its reading is compared with what the piece actually set. A line it read differently is a legibility problem; a figure it saw that the piece never set is flagged, and the agent has to look at that exact frame and say whether it was a misreading or a real fault. 


Agents can't verify their own work. Every verdict is recorded by the tool that ran the check, never by the agent, and a check that compared nothing (an image with no text read back) fails rather than passes. A check that is still failing when a piece is delivered stays on the Verification tab marked as failing: it is reported, never hidden or rewritten as a pass.

### Deliverables
The end result of a Polson graphics design run is the finished media production together with a set of deliverables which mirror the deliverables from a human-led graphic design project:
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/deliverables.png)

## Inspiration

### The possibility of agentic graphic design
Programmers today are accustomed to collaborating with LLM-based agents on developing software, and the astonishing levels of productivity and creativity that these collaborations enable.  The entire software development industry is being transformed, for better or worse, by the automation of all aspects of software development by agents. But no equivalent creative collaboration and automation has taken root in the field of visual arts or graphic design.

Neural models that generate and manipulate images and other visual artifacts suffer from a core problem in modern artificial intelligence: the innate inability of neural models to handle prolonged, iterative interaction over non-language data that maintains a consistent level of *precision*, *coherency*, and *verifiability*. Language in all its human (and artificial) varieties is discrete, segmentable, addressable. Visual information is continuous, non-segmented, and entangled. When you ask a diffusion-based model like Midjourney to create or change an image, it follows a completely different process compared to asking an LLM to create or make a change to a program's source code or to edit a podcast script. There are no precise, deterministic tools a diffusion-based model can use to attempt precise operations on an image like "make the hat on the man in the third row red" or "change the figure in the third row to $550,000", in such a way that the objects and properties referred to in the operation retain the permanence humans are accustomed to when thinking and speaking and acting. There are also very limited environmental tools for models to check and validate the text and figures embedded in a visual artifact, the way program code can be checked and validated and tested. This makes agentic graphic design work for things like presentations of financial reports and data very difficult.

An agentic graphic and motion design system that is precise, accurate and reliable enough for medium-horizon tasks like creating videos for VC pitches or analysis of S-1 filings while being able to create design variants that can autonomously adapt to audiences in different cultures or regions requires a fundamentally different approach to agentic systems for image and video generation today. 

Such a system would give ordinary people the power to create professional graphic design productions on their own without needing years of specialized knowledge and training. It would also give professional graphic designers the ability to automate lower-level tasks and projects and allow them to reach higher levels of productivity and creativity and autonomy.

Overall such a system has the potential to enable the same revolutionary collaboration and automation for graphic design as exists for software development today. 
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/output-2.webp)



## How it works
Polson Graphics Studio is designed around patterns from human-powered creative-as-a-service (CaaS) platforms. From the Polson studio interface the user creates a new request for a graphic design project
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/dashboard-new.PNG)

They set the options for the request including the audiences that will be targeted by the campaign:
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/barbershop-select-audience.png)

Once the project run starts the agent works autonomously according to the briefs and documents the user provided. All agent activity is logged in the studio interface where the user can inspect it:
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/agent-activity.PNG)

If the agent needs input from the user, it will prompt through the interface:
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/interaction1.png)


## How we built it

### Engine

The Polson [drawing engine](https://github.com/allisterb/Polson/blob/master/src/Polson.MCPServer/JsDrawingEngine.cs) is a JavaScript procedural code-as-state drawing engine that uses [Skia](https://skia.org/) for rendering. It has both a 2D vector API compatible with [Snap.svg](https://github.com/adobe-webplatform/Snap.svg), and a 2D raster API with a Canvas2D compatible surface, as well as the conventional Skia 2D API, including Skia [SkSL](https://skia.org/docs/user/sksl/) shaders. The engine does not rely on an embedded browser for executing JavaScript or rendering which allows it to be extremely fast for drawing. Snap.svg and Canvas2D APIs were chosen simply for how much JavaScript code currently exists for drawing using these 2 API shapes that LLMs would be exposed to. Both vector and raster APIs in Polson draw directly to Skia canvases where they can be rendered as bitmaps in different image formats like WebP, or if using the vector API as SVGs that can be consumed by programs like Illustrator or Inkscape.

### Animation
The Polson [procedural animation API](https://github.com/allisterb/Polson/tree/master/src/Polson.Animation) was ported from the [Synfig Studio](https://github.com/synfig/synfig) and [EffectCraft](https://github.com/storytold/effectcraft/) projects. Like the drawing API, agents write JavaScript code to create animations then check and verify the animation visually, and it can be rendered to different video formats like `.webp` without requiring a browser. The engine supports different languages and is capable of producing many different drawing and text and camera effects:
![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/becool_output.webp)
### Code Mode MCP Server
The Polson drawing and animation engine is exposed to agents as a 'Code Mode' MCP server. Code Mode is a technique for [programmatic tool calling](https://platform.claude.com/cookbook/tool-use-programmatic-tool-calling-ptc) by agents using a code execution environment described by [Cloudflare](https://blog.cloudflare.com/code-mode-mcp/) and [Anthropic](https://www.anthropic.com/engineering/code-execution-with-mcp). Instead of requiring the agent to call individual drawing tools one at a time with a full model round-trip with each result, the agent writes an entire JavaScript program against a typed SDK to complete one stage of the graphic or art production process. 
 

### Workflows
A workflow is the set of inputs, prompts, guardrails, steps, design manuals, resources, and deliverables an agent uses for one kind of graphic production, e.g the [pitch workflow](https://github.com/allisterb/Polson/blob/master/src/Polson.CLI/ProjectTemplate/motion/type.pitch.md). Each workflow has a set of stages e.g. the **`vector_infographic`** workflow first researches its subject, then composes the infographic, then verifies as far as possible that the figures it drew are accurate: 
![The vector_infographic workflow](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/vector-infographic-workflow.svg)

The deliverable is an SVG infographic that can be opened in Illustrator with the data being  visualized and validated as much as possible:

![](https://ajb.nyc3.cdn.digitaloceanspaces.com/polson/dmd_final_a.svg)

