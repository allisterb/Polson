"""A `nebius/` model goes through LiteLLM; a Gemini model stays a string and never imports it.

Runs under both venvs: `python-adk/` (no litellm — the Qloo runtime) and `python-nebius-adk/` (with it).
"""

import importlib.util
import os
import sys
import unittest
from pathlib import Path
from unittest import mock

# `src/` first, then `adk_agent/` on top: two packages here are called `studio`, and the ADK one has to win.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import studio  # noqa: E402

HAS_LITELLM = importlib.util.find_spec("litellm") is not None


class ModelForTests(unittest.TestCase):
    def test_a_gemini_model_stays_a_name_and_imports_nothing(self):
        before = "litellm" in sys.modules
        self.assertEqual(studio.model_for("gemini-3.8-flash"), "gemini-3.8-flash")
        self.assertEqual("litellm" in sys.modules, before, "a Gemini model must not import LiteLLM")

    def test_only_a_provider_prefix_means_litellm(self):
        self.assertTrue(studio.is_litellm("nebius/nvidia/Nemotron-3.5-Super-VL"))
        for name in ("gemini-3.8-flash", "gemini-3.7-flash", "models/gemini-3.8-flash"):
            with self.subTest(name=name):
                self.assertFalse(studio.is_litellm(name))

    def test_the_output_allowance_is_for_litellm_models_only(self):
        """Nemotron spent a 4,000-token ceiling on reasoning and never answered; Gemini keeps its default."""
        self.assertIsNone(studio._retry_config("gemini-3.8-flash").max_output_tokens)
        self.assertIsNone(studio._retry_config().max_output_tokens)
        self.assertEqual(studio._retry_config("nebius/nvidia/Nemotron-3.5-Super-VL").max_output_tokens,
                         studio.LITELLM_MAX_OUTPUT_TOKENS)
        # The retries carry over: ADK's adapter reads attempts as LiteLLM's num_retries.
        self.assertEqual(studio._retry_config("nebius/x").http_options.retry_options.attempts, studio.RETRY_ATTEMPTS)

    @unittest.skipUnless(HAS_LITELLM, "litellm is only in python-nebius-adk")
    def test_a_nebius_model_is_a_litellm_on_the_accounts_endpoint(self):
        from google.adk.models.lite_llm import LiteLlm

        with mock.patch.dict(os.environ, {"NEBIUS_API_BASE": "https://api.example.test/v1"}):
            os.environ.pop("LITELLM_LOCAL_MODEL_COST_MAP", None)
            model = studio.model_for("nebius/nvidia/Nemotron-3.5-Super-VL")
            self.assertEqual(os.environ.get("LITELLM_LOCAL_MODEL_COST_MAP"), "True",
                             "LiteLLM fetches its price list from GitHub at import unless told not to")
        self.assertIsInstance(model, LiteLlm)
        self.assertEqual(model.model, "nebius/nvidia/Nemotron-3.5-Super-VL")
        self.assertEqual(model._additional_args.get("api_base"), "https://api.example.test/v1")

    @unittest.skipIf(HAS_LITELLM, "only meaningful where litellm is absent")
    def test_without_litellm_a_nebius_model_fails_loudly(self):
        with self.assertRaises(ImportError):
            studio.model_for("nebius/nvidia/Nemotron-3.5-Super-VL")


if __name__ == "__main__":
    unittest.main()
