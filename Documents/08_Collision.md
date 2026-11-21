# ZTX16 — Collision API

## Scope

Collision is a minimal geometry/query system, not a physics engine.

It is independent of Graphics, Graphics Layers and Level.

A Collider may exist independently of a Level.

Game logic may organize Colliders into arbitrary sets. The collision system operates against explicitly supplied sets.

## Collider

Base v1 supports only Rectangle Collider.

State:

- Position
- Size
- Enabled

Position is the lower-left corner.

No Group, Mask, physics parameters or semantic type are stored by a Collider.

## Collider sets

Game code may create logical sets such as:

- Solid Objects
- Enemies
- Damageable
- Targetable
- Triggers

A Collider may belong to multiple sets.

Sets are game-logic organization, not a built-in collision semantic hierarchy.

## Queries

Base query categories:

- Rectangle ↔ Rectangle overlap;
- Point ↔ Rectangle;
- Moving Rectangle → Collider Set, first collision;
- Rectangle → Collider Set, overlaps.

A moving rectangle query considers the full motion from start to requested target and returns the earliest relevant collision.

## Collision result

Where applicable a collision result may contain:

- collision occurred;
- Collider hit;
- collision point;
- collision normal;
- safe position before collision.

No distance/time-to-collision is part of the base result.

## Disabled colliders

Disabled Colliders do not participate in queries, but retain their state.

## No physics

Collision does not automatically apply:

- forces;
- impulses;
- gravity;
- velocity;
- friction;
- restitution;
- sliding;
- step climbing;
- rigid-body simulation.

Game code decides what to do with query results.

## Collision Debug

Collision Debug is a separate visualization API.

It may draw wireframe rectangle representations of active/enabled Colliders into a graphics target.

Debug color has no collision semantics.

Debug visualization does not modify collision state and is not part of the normal collision system.

## Removed/obsolete concepts

The following old draft concepts are obsolete and must not be implemented in the base model:

- static 256×256 Collision Map;
- Collision Layer;
- Group + Mask filtering;
- screen-only collision restriction;
- built-in physics behavior.
