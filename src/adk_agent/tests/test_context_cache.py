"""`studio.context_cache_config`: ADK's explicit Gemini cache, off unless asked for, and bounded when it is."""

from __future__ import annotations

import os
import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import studio  # noqa: E402


class ContextCacheTests(unittest.TestCase):

    def test_off_unless_asked_for(self):
        for value in ("", "0", "yes"):
            with mock.patch.dict(os.environ, {studio.CONTEXT_CACHE_ENV: value}):
                self.assertIsNone(studio.context_cache_config(), value)

    def test_on_with_long_life_and_few_rebuilds(self):
        with mock.patch.dict(os.environ, {studio.CONTEXT_CACHE_ENV: "1", "POLSON_CONTEXT_CACHE_TTL": "",
                                          "POLSON_CONTEXT_CACHE_INTERVALS": "", "POLSON_CONTEXT_CACHE_MIN_TOKENS": ""}):
            config = studio.context_cache_config()
        self.assertEqual((3600, 25, 32768), (config.ttl_seconds, config.cache_intervals, config.min_tokens))

    def test_settings_are_clamped_to_what_adk_accepts(self):
        """ADK refuses more than 100 uses; a typo falls back to the default rather than failing the app."""
        with mock.patch.dict(os.environ, {studio.CONTEXT_CACHE_ENV: "1", "POLSON_CONTEXT_CACHE_INTERVALS": "500",
                                          "POLSON_CONTEXT_CACHE_TTL": "an hour"}):
            config = studio.context_cache_config()
        self.assertEqual((100, 3600), (config.cache_intervals, config.ttl_seconds))


class RequestHeadTests(unittest.TestCase):
    """The turn log's `head=`: the same instruction and tools give the same fingerprint, and one byte changes it."""

    @staticmethod
    def request(instruction: str, tool: str):
        from google.adk.models.llm_request import LlmRequest
        from google.genai import types

        return LlmRequest(config=types.GenerateContentConfig(
            system_instruction=instruction,
            tools=[types.Tool(function_declarations=[types.FunctionDeclaration(name=tool, description="d")])]))

    def test_same_head_same_fingerprint(self):
        self.assertEqual(studio.request_head(self.request("draw", "peek")), studio.request_head(self.request("draw", "peek")))

    def test_each_half_says_what_changed(self):
        base = studio.request_head(self.request("draw", "peek")).split("/")
        said = studio.request_head(self.request("draw.", "peek")).split("/")
        tools = studio.request_head(self.request("draw", "peek2")).split("/")
        self.assertEqual((base[0] != said[0], base[1] == said[1]), (True, True))
        self.assertEqual((base[0] == tools[0], base[1] != tools[1]), (True, True))

    def test_anything_odd_is_a_question_mark_not_a_failure(self):
        self.assertRegex(studio.request_head(object()), r"^[0-9a-f]{8}/[0-9a-f]{8}$|^\?$")


if __name__ == "__main__":
    unittest.main()
