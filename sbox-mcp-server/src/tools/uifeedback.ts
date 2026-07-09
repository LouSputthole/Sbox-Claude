import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * UI / Feedback pack — three feedback scaffolds (v1.20.0, Track C):
 *
 *   - create_worldpanel_ui   diegetic clickable WorldPanel Razor UI (+ scss)
 *   - create_proxy_nametag   billboarded owner-name tag above a networked player
 *   - create_combo_meter     combo counter + decay + multiplier (.cs) + Razor HUD
 *
 * All are file/scene-mutating (refused during play mode by the bridge dispatch).
 * The Razor output is razor_lint-safe by construction (BuildHash override, no
 * switch-expressions or non-ASCII in @code, class-selector SCSS roots), modeled
 * on create_leaderboard_panel.
 */
export function registerUiFeedbackTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── create_worldpanel_ui ──────────────────────────────────────────
  server.tool(
    "create_worldpanel_ui",
    "Generate a diegetic, clickable world-space UI: a Razor PanelComponent (+ .razor.scss) meant to sit on a GameObject that ALSO carries a Sandbox.WorldPanel — the WorldPanel is the world-space render surface (PanelSize / RenderScale / InteractionRange), this component is the actual UI it renders (PanelComponent has no world-panel mode of its own). Ships two example buttons wired to @onclick that raise a static event OnButtonPressed(string id), so game code reacts WITHOUT editing the panel (subscribe: <Name>.OnButtonPressed += id => ...). razor_lint-safe by construction (BuildHash override, no switch-expressions / non-ASCII in @code, class-selector SCSS root). SCENE PREREQUISITE FOR CLICKS: a WorldPanel's buttons only fire when a Sandbox.WorldInput exists in the scene (typically on the camera or the player) with its LeftMouseAction set to your click input action (e.g. \"attack1\"); on this SDK WorldInput drives itself from the camera + that action — there is no manual ray to feed, and WorldInput.Hovered (read-only) reflects the panel under the cursor. Without a WorldInput present, @onclick never fires. Setup: add_world_panel to a GameObject, add this component to the same object, add a WorldInput to the scene.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated panel. Defaults to 'WorldPanelUi'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .razor + .razor.scss. Defaults to 'Code/UI'"),
      title: z
        .string()
        .optional()
        .describe("Heading text baked into the panel (editable per-instance via the Title [Property]). Defaults to 'Interact'"),
    },
    async (params) => {
      const res = await bridge.send("create_worldpanel_ui", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_proxy_nametag ──────────────────────────────────────────
  server.tool(
    "create_proxy_nametag",
    "Generate a sealed Component that floats the OWNER'S display name above a networked player. TextRenderer-based (not WorldPanel): a nametag is one short string with a distance fade, so a TextRenderer on a managed child object is far simpler than a WorldPanel + Razor + WorldInput stack — no UI assets, no panel host, per-frame alpha is a one-liner (mirrors create_floating_combat_text). Reads GameObject.Network.Owner.DisplayName (Owner and OwnerConnection are the same Connection on this SDK; falls back to the object name offline). Visibility is the INVERSE of the usual proxy guard: it renders only when GameObject.Network.IsProxy is true — i.e. on OTHER clients' copies of the player — so you never see a tag over your own head (offline / no networking => IsProxy false everywhere => no tags, expected). Spawns a CHILD GameObject for the text so billboarding never rotates the player model; cleaned up on disable. [Property] MaxDistance fades the tag out with distance, HeightOffset floats it above the head, FontSize sizes it. Attach to the ROOT of your networked player object. Pairs with create_networked_player (add it to the generated player prefab so every remote player is labeled). Returns { created, path, className, maxDistance, heightOffset, note, nextSteps }. Follow with trigger_hotload, then get_compile_errors, then attach via add_component_with_properties (component=className).",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'ProxyNametag'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      maxDistance: z
        .number()
        .optional()
        .describe("Full alpha up close; fades to zero as the camera approaches this distance and is hidden past it, in world units. Defaults to 2000"),
      heightOffset: z
        .number()
        .optional()
        .describe("Height above the object's origin to float the tag, in world units (~72 clears a Citizen's head). Defaults to 72"),
    },
    async (params) => {
      const res = await bridge.send("create_proxy_nametag", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_combo_meter ────────────────────────────────────────────
  server.tool(
    "create_combo_meter",
    "Generate a combo system: a sealed Component (the authoritative, headless state) PLUS a small Razor HUD (PanelComponent + scss). Three files: <Name>.cs + <Name>Hud.razor + <Name>Hud.razor.scss. The component exposes a static Bump() — call it on every hit and the Count rises; an idle window (ComboWindowSeconds, tracked with TimeSince) resets it; the Multiplier steps up through [Property] tier thresholds (Tier2Hits/Tier3Hits/Tier4Hits => 2x/3x/4x); and a static OnComboChanged(int count, float multiplier) event fires so any HUD/audio reacts without a reference (Bump() targets the active instance, so callers never need a handle — attach ONE to a persistent object). The HUD subscribes to OnComboChanged, shows \"<count> HITS x<mult>\", and pulses via a CSS animation on every change (razor_lint-safe: BuildHash folds count/mult/pulse, no switch-expressions / non-ASCII in @code, class-selector SCSS root; host it under a ScreenPanel via add_screen_panel). Pairs with create_health_system / create_floating_combat_text — call Bump() from the damage path and Spawn a popup that reflects the multiplier.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the combo component; the HUD is generated as <Name>Hud. Defaults to 'ComboMeter'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs + .razor + .razor.scss. Defaults to 'Code'"),
      comboWindowSeconds: z
        .number()
        .optional()
        .describe("Idle seconds before the combo resets back to zero (clamped to >= 0.25). Defaults to 3"),
    },
    async (params) => {
      const res = await bridge.send("create_combo_meter", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
