import { z } from "zod";

/**
 * Shared Zod schemas for the cross-language vector / rotation contract.
 *
 * Every value is passed through to the bridge UNCHANGED; the C# handlers
 * (ClaudeBridge.ParseVector3 / ParseRotation) are the source of truth for
 * parsing and accept both the object and the comma-string form. Keeping the
 * shapes here means a change to the contract is made once, not in every tool
 * module (GitHub issue #22).
 *
 * The manifest extractor (scripts/extract-manifest.mjs) shape-detects vectors
 * by their exact key set, so the object shapes below must stay {x,y,z} and
 * {pitch,yaw,roll}. camera.ts / diagnostics.ts keep their own STRICT variants
 * (finite numbers, `.strict()`) on purpose — they are not duplicates.
 */

/** Object form of a 3D vector. */
export const Vector3Object = z.object({
  x: z.number().describe("X coordinate"),
  y: z.number().describe("Y coordinate"),
  z: z.number().describe("Z coordinate"),
});

/** A 3D vector as EITHER an object {x,y,z} OR a comma string "x,y,z". */
export const Vector3Schema = z
  .union([
    Vector3Object,
    z.string().describe('Comma string "x,y,z", e.g. "0,0,200"'),
  ])
  .describe('3D vector — object {x,y,z} OR comma string "x,y,z"');

/** Object form of a Euler rotation in degrees. */
export const RotationObject = z.object({
  pitch: z.number().describe("Pitch angle in degrees"),
  yaw: z.number().describe("Yaw angle in degrees"),
  roll: z.number().describe("Roll angle in degrees"),
});

/** A Euler rotation as an object {pitch,yaw,roll} in degrees. */
export const RotationSchema = RotationObject.describe(
  "Euler rotation with pitch, yaw, roll in degrees"
);

/** A Euler rotation as EITHER an object {pitch,yaw,roll} OR a comma string "pitch,yaw,roll". */
export const RotationOrStringSchema = z
  .union([
    RotationObject,
    z.string().describe('Comma string "pitch,yaw,roll", e.g. "0,90,0"'),
  ])
  .describe('Euler rotation: object {pitch,yaw,roll} OR comma string "pitch,yaw,roll"');
