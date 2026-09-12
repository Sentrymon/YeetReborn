# Change log

## Version 1.1.1
  + Moved the check for configLib that would cause the mod to not load if ConfigLib was not found

## Version 1.1

### Client side changes

  + Added six new yeet sounds - Quack, Yeet, Wilhelm, Aztec, Glass and Chicken. Explore and have fun!
  + Added chat controls: .yeetsound, .yeetvol, .yeetstrength, .yeetlock, .yeetpitch
  + Added `.yeet` to list all yeet commands, also reachable as `.yeethelp`
  + Added ConfigLib compatibility
  + Fixed shockwave rings spawning forever on an item that landed in water, which left a cloud of
    foam-like particles floating on the surface
  + Fixed long throws freezing in mid air
  + Fixed the yeet sound db setting

### Server side changes

  + Added a Yeet! Server Settings section to the ConfigLib screen
  + Added a maximum throw strength that sets player max.
  + Added volume max
  + Added a sound range setting, in blocks
  + Added a toggle for the exertion 'grunt'

## Version 1.0

  + Press Y to yeet the item in your active hotbar slot in a high arc
  + Press Ctrl+Y to yeet the entire stack
  + Items leave your hand at a fixed 45 degree angle and then follow the game's own physics
  + Puffy shockwave rings trail the item in flight
  + Both keys are rebindable under Settings > Controls
