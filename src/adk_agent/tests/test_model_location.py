"""`studio.model_for`: model calls sent to a named location, signed with the express key.

An express key is routed to us-central1, and `gemini-4-argon` is served only at `locations/global` (probed
2026-10-28: OK there, 404 at us-central1). These check the client's routing without calling anything.
"""

from __future__ import annotations

import os
import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import studio  # noqa: E402

KEY = {"GOOGLE_API_KEY": "AQ.test-key-not-real"}


class ModelLocationTests(unittest.TestCase):
    def test_without_a_location_the_model_is_its_name_as_before(self):
        with mock.patch.dict(os.environ, {**KEY}, clear=False):
            os.environ.pop(studio.MODEL_LOCATION_ENV, None)
            self.assertEqual(studio.model_for("gemini-3.8-flash"), "gemini-3.8-flash")

    def test_a_location_roots_every_call_there_and_keeps_the_key_and_the_name(self):
        env = {**KEY, studio.MODEL_LOCATION_ENV: "global", studio.MODEL_PROJECT_ENV: "polson"}
        with mock.patch.dict(os.environ, env):
            model = studio.model_for("gemini-4-argon")
        self.assertEqual(model.model, "gemini-4-argon", "the name everything else reads stays the model's own")
        api = model.api_client._api_client
        self.assertEqual(api._http_options.base_url,
                         "https://aiplatform.googleapis.com/v1beta1/projects/polson/locations/global/")
        self.assertEqual(api.api_key, KEY["GOOGLE_API_KEY"])

    def test_a_location_without_its_project_or_a_key_is_refused_with_what_to_set(self):
        with mock.patch.dict(os.environ, {**KEY, studio.MODEL_LOCATION_ENV: "global"}):
            os.environ.pop(studio.MODEL_PROJECT_ENV, None)
            with self.assertRaisesRegex(studio.ConfigError, studio.MODEL_PROJECT_ENV):
                studio.model_for("gemini-4-argon")
        with mock.patch.dict(os.environ, {studio.MODEL_LOCATION_ENV: "global", studio.MODEL_PROJECT_ENV: "polson"}):
            os.environ.pop("GOOGLE_API_KEY", None)
            with self.assertRaisesRegex(studio.ConfigError, "GOOGLE_API_KEY"):
                studio.model_for("gemini-4-argon")


if __name__ == "__main__":
    unittest.main()
