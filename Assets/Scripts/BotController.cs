using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent), typeof(Health))]
public class BotController : MonoBehaviour
{
    public Weapon weapon;
    public Transform head;
    public float viewRange = 40f;
    public float fieldOfView = 120f;
    public float stopDistance = 12f;
    public float reactionTime = 0.4f;
    public float turnSpeed = 8f;
    public float memoryTime = 4f;
    public float arenaHalfSize = 22f;
    public float alertTime = 2f;
    [Tooltip("bots with a sniper rifle do not scope closer than this")] public float noScopeDistance = 8f;
    float alertUntil;
    // heard a noise or was hit by an enemy a moment ago: looks all around, not only in its field of view
    public bool IsAlerted => Time.time < alertUntil;

    [Header("Melee")]
    [Tooltip("a bot with a knife walks up to this distance")] public float meleeStopDistance = 1.3f;

    [Header("Bhop")]
    public float hopHeight = 1.2f;
    public float hopGravity = 20f;
    public float airSpreadMultiplier = 2f;
    [Tooltip("hops only on a run at least this long, never in a fight")] public float hopMinDistance = 10f;
    [Tooltip("the next corner of the path must be at least this far, so the bot hops only on straight parts")] public float hopMinStraight = 4f;
    [Tooltip("chance to hop on a long run")] [Range(0f, 1f)] public float hopChance = 0.5f;
    [Tooltip("jumps in one chain, min and max")] public Vector2Int hopsPerChain = new Vector2Int(3, 6);
    [Tooltip("pause after a chain, min and max seconds")] public Vector2 hopCooldown = new Vector2(4f, 8f);
    Bhop bhop;
    float hopTime = -1f;     // below zero: on the ground
    // in the air of a hop (CharacterBody shows the jump pose instead of the run)
    public bool IsHopping => hopTime >= 0f;
    float baseOffset;
    int hopsLeft;            // jumps left in the current chain
    bool runDecided;         // already decided whether to hop on this run
    float nextChainAt;

    [Header("Sneak (Shift)")]
    [Tooltip("speed while sneaking, like the player's Shift")] public float sneakSpeedFactor = 0.5f;
    [Tooltip("a bot may sneak when the place it checks is closer than this (or when it is alerted)")] public float sneakDistance = 25f;
    [Tooltip("chance to sneak, decided once each time the bot goes to check an enemy")] [Range(0f, 1f)] public float sneakChance = 0.6f;
    // walks at sneakSpeedFactor without footsteps (GameAudio) and does not hop
    public bool IsSneaking { get; private set; }
    bool sneakDecided;       // already decided whether to sneak on this approach

    [Header("Crouch")]
    [Tooltip("chance to crouch in a fight, decided once per enemy: the bot crouches while it stands and shoots at him")]
    [Range(0f, 1f)] public float crouchChance = 0.4f;
    [Tooltip("crouches in a fight only when the enemy is at least this far (medium and long range)")] public float crouchMinDistance = 7f;
    [Tooltip("chance to crouch behind cover while reloading after a fight")] [Range(0f, 1f)] public float coverCrouchChance = 0.7f;
    [Tooltip("something between the bot and the last known enemy closer than this is cover")] public float coverDistance = 2.5f;
    [Tooltip("capsule height when fully crouched (standing: the prefab's height, 2)")] public float crouchHeight = 1.3f;
    [Tooltip("the eyes (head) go down this much at full crouch, like the player's camera (0.6 -> 0.05)")] public float crouchEyeDrop = 0.55f;
    [Tooltip("spread multiplier at full crouch, like the player: crouchSpread / standSpread = 0.4 / 0.8")] public float crouchSpread = 0.5f;
    public float crouchSpeedFactor = 0.5f;
    [Tooltip("1/s: 0 -> 1 in about 0.15 s, like the player")] public float crouchChangeSpeed = 7f;
    // 0 = standing, 1 = crouched; CharacterBody poses the model with it. "-crouch <0..1>" sets crouchChance (0 = never).
    public float CrouchAmount => crouchAmount;
    public bool IsCrouching => crouchAmount > 0.5f;
    // the enemy the bot fights now (null when it sees nobody)
    public Health Target => target;
    float crouchAmount;
    float appliedCrouch = -1f;
    bool crouchInFight;      // decided when the bot sees a new enemy
    bool coverCrouch;        // reloading crouched behind cover
    bool reloadSeen;
    CapsuleCollider capsule;
    float standHeight;
    Vector3 standCenter;
    Vector3 standHead;
    static float crouchArg = -2f;   // -2 = not read yet, -1 = not on the command line

    NavMeshAgent agent;
    MapBounds bounds;
    Health self;
    Stealth stealth;
    Health target;
    Vector3 lastKnown;
    bool hasLastKnown;
    float lastSeenAt;
    float seenTime;
    float baseSpeed;
    // standing still: the freeze time at the round start or the bots frozen from the menu (RoundManager)
    bool frozen;

    // a bot sees a part of the body, not only its centre: over low cover only the head shows,
    // at a corner only a shoulder. It aims at the part it sees.
    const float HeadBelowTop = 0.25f;     // head point, meters below the top of the body
    const float ShoulderOffset = 0.3f;    // shoulder points, meters to the side of the line of sight
    // a character model (CharacterBody) gives up to 7 points from its hitboxes, the capsule 5
    readonly Vector3[] bodyPoints = new Vector3[8];
    readonly RaycastHit[] rayHits = new RaycastHit[32];
    Vector3 enemyPoint;                   // the visible point of the enemy found this step

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        self = GetComponent<Health>();
        agent.updateRotation = false;
        baseSpeed = agent.speed;
        baseOffset = agent.baseOffset;
        bounds = FindAnyObjectByType<MapBounds>();

        // standing shape, crouching makes the capsule lower and moves the eyes down
        capsule = GetComponent<CapsuleCollider>();
        if (capsule != null)
        {
            standHeight = capsule.height;
            standCenter = capsule.center;
        }
        if (head != null) standHead = head.localPosition;

        float arg = CrouchArg();
        if (arg >= 0f)
        {
            crouchChance = arg;
            if (arg <= 0f) coverCrouchChance = 0f;
        }
    }

    // "-crouch <0..1>" on the command line (simulations): the chance to crouch in a fight, 0 turns crouching off
    static float CrouchArg()
    {
        if (crouchArg > -1.5f) return crouchArg;
        crouchArg = -1f;
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] != "-crouch") continue;
            if (float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out float v))
                crouchArg = Mathf.Clamp01(v);
        }
        return crouchArg;
    }

    void OnEnable()
    {
        target = null;
        hasLastKnown = false;
        seenTime = 0f;
        alertUntil = 0f;
        hopTime = -1f;
        hopsLeft = 0;
        runDecided = false;
        nextChainAt = 0f;
        IsSneaking = false;
        sneakDecided = false;
        crouchAmount = 0f;
        crouchInFight = false;
        coverCrouch = false;
        reloadSeen = false;
        ApplyCrouch();
        agent.baseOffset = baseOffset;
        self.Damaged += OnDamaged;
    }

    void OnDisable()
    {
        self.Damaged -= OnDamaged;
        IsSneaking = false;
    }

    // a noise (the player's steps or shots, see GameAudio): bots of the other team within the radius hear it
    public static void Noise(Vector3 position, float radius, Health source)
    {
        if (source == null) return;
        float r2 = radius * radius;
        foreach (Health h in Health.All)
        {
            if (h.IsDead || !h.gameObject.activeInHierarchy || h.team == source.team) continue;
            if ((h.transform.position - position).sqrMagnitude > r2) continue;
            BotController bot = h.GetComponent<BotController>();
            if (bot != null) bot.Hear(position);
        }
    }

    // the bot turns to the noise and goes to check it, unless it is already fighting
    void Hear(Vector3 position)
    {
        if (!isActiveAndEnabled || target != null) return;
        alertUntil = Time.time + alertTime;
        lastKnown = position;
        hasLastKnown = true;
        lastSeenAt = Time.time;
    }

    void OnDamaged(Health attacker)
    {
        if (attacker.team == self.team) return;
        alertUntil = Time.time + alertTime;
        lastKnown = attacker.transform.position;
        hasLastKnown = true;
        lastSeenAt = Time.time;
    }

    void FixedUpdate()
    {
        if (stealth == null) stealth = GetComponent<Stealth>();
        if (bhop == null) bhop = GetComponent<Bhop>();

        // no scope while the weapon is still being drawn
        if (weapon != null && weapon.IsDrawing) weapon.Scoped = false;

        // the freeze time at the round start, or the bots frozen from the menu: the bot stands still and does
        // nothing, it can still be shot and killed (both flags are never set in simulations)
        if (RoundManager.InFreezeTime || RoundManager.BotsFrozen)
        {
            FrozenStep();
            return;
        }
        if (frozen) Unfreeze();

        bool wantHop = false;
        Health enemy = FindVisibleEnemy();
        bool checking = hasLastKnown && Time.time - lastSeenAt < memoryTime;
        bool alerted = Time.time < alertUntil;
        UpdateSneak(enemy == null && checking, alerted);

        float bhopFactor = bhop != null ? bhop.SpeedMultiplier : 1f;
        // slower while firing for some weapons (R8, G3SG1), like the player
        float weaponFactor = weapon != null ? weapon.CurrentMoveSpeedFactor : 1f;
        float sneakFactor = IsSneaking ? sneakSpeedFactor : 1f;
        float crouchFactor = Mathf.Lerp(1f, crouchSpeedFactor, crouchAmount);
        agent.speed = (stealth != null ? baseSpeed * weaponFactor * stealth.SpeedFactor() : baseSpeed * weaponFactor) * bhopFactor * sneakFactor * crouchFactor;

        bool wantCrouch = false;
        if (enemy != null)
        {
            bool newEnemy = enemy != target;
            if (newEnemy) seenTime = 0f;
            target = enemy;
            lastKnown = enemy.transform.position;
            hasLastKnown = true;
            lastSeenAt = Time.time;
            seenTime += Time.fixedDeltaTime;
            coverCrouch = false;

            Face(enemy.transform.position - transform.position);

            float distance = Vector3.Distance(transform.position, enemy.transform.position);
            // a bot with a knife (or the short Zeus) walks up close, with a gun it keeps its distance
            bool close = weapon.IsMelee || weapon.IsTaser;
            float stop = close ? meleeStopDistance : stopDistance;
            if (distance > stop)
            {
                agent.SetDestination(enemy.transform.position);
            }
            else
            {
                agent.ResetPath();
            }

            // some fights the bot crouches while it stands and shoots at medium or long range,
            // only if it still sees the enemy from the lower eyes
            // (no random draw when crouching is off, so "-crouch 0" runs exactly like before)
            if (newEnemy) crouchInFight = crouchChance > 0f && !weapon.IsMelee && Random.value < crouchChance && SeesFromCrouch(enemyPoint);
            wantCrouch = crouchInFight && distance <= stop && distance >= crouchMinDistance && hopTime < 0f;

            if (seenTime >= reactionTime)
            {
                float blind = GrenadeSystem.GetBlindness(self);
                Vector3 aimPoint = enemyPoint;
                if (blind > 0.05f)
                {
                    float seed = (self != null ? self.name.GetHashCode() : 0) * 0.01f;
                    Vector3 offset = new Vector3(
                        Mathf.Sin(Time.time * 13f + seed) * 1.4f,
                        Mathf.Cos(Time.time * 9f + seed) * 0.7f,
                        Mathf.Sin(Time.time * 17f + seed) * 1.4f) * blind;
                    aimPoint += offset;
                }
                head.LookAt(aimPoint);
                // like the player, a bot cannot scope or shoot until its weapon is drawn
                weapon.Scoped = !weapon.IsDrawing && distance > noScopeDistance && blind < 0.2f;
                float speedRatio = agent.velocity.magnitude / Mathf.Max(baseSpeed, 0.01f);
                weapon.ExternalMoveInaccuracy = hopTime >= 0f ? 1.5f : Mathf.Clamp01(Mathf.InverseLerp(0.25f, 0.8f, speedRatio));
                if (blind > 0.05f) weapon.ExternalSpreadMultiplier *= 1f + 6f * blind;

                // the knife and the Zeus only when the enemy is in their reach
                float reach = weapon.IsMelee ? weapon.data.meleeRange : weapon.IsTaser ? weapon.range : float.MaxValue;
                if (!weapon.IsDrawing && distance <= reach)
                    weapon.TryFire();
            }
        }
        else
        {
            target = null;
            seenTime = 0f;
            if (weapon.Ammo < weapon.magazineSize) weapon.StartReload();

            // after a fight a bot may reload crouched behind cover instead of walking on (decided once per reload)
            bool reloading = weapon.IsReloading;
            if (reloading && !reloadSeen)
            {
                reloadSeen = true;
                coverCrouch = checking && hopTime < 0f && coverCrouchChance > 0f && Random.value < coverCrouchChance && HasCover();
            }
            else if (!reloading)
            {
                reloadSeen = false;
                coverCrouch = false;
            }

            if (coverCrouch)
            {
                agent.ResetPath();
                wantCrouch = true;
                Face(lastKnown - transform.position);
            }
            else
            {
                if (checking)
                    agent.SetDestination(lastKnown);
                else
                    Wander();

                Face(agent.desiredVelocity);
            }

            // a sneaking or crouching bot walks, it does not hop
            bool longRun = !alerted && !IsSneaking && !coverCrouch && crouchAmount <= 0f && agent.hasPath && !agent.pathPending
                           && agent.remainingDistance > hopMinDistance
                           && Vector3.Distance(transform.position, agent.steeringTarget) > hopMinStraight;
            wantHop = WantHop(longRun);
        }

        // in a fight the chain is broken
        if (enemy != null && hopsLeft > 0) EndChain();

        UpdateCrouch(wantCrouch);
        UpdateBhop(wantHop);
    }

    // Frozen: the agent is stopped without a path (no walking, no chase), no new hop (one in the air lands), no shot
    // and no reload; the bot stands up and forgets its target, it looks for enemies again when the freeze ends.
    // Damage still works as usual (OnDamaged only remembers where the attacker was).
    void FrozenStep()
    {
        if (!frozen)
        {
            frozen = true;
            if (hopsLeft > 0) EndChain();
            runDecided = false;
            IsSneaking = false;
            sneakDecided = false;
        }
        if (agent.isOnNavMesh)
        {
            if (agent.hasPath) agent.ResetPath();
            agent.isStopped = true;
            agent.velocity = Vector3.zero;
        }
        target = null;
        seenTime = 0f;
        coverCrouch = false;
        UpdateCrouch(false);
        UpdateBhop(false);
    }

    void Unfreeze()
    {
        frozen = false;
        if (agent.isOnNavMesh) agent.isStopped = false;
    }

    // crouching: lower capsule (bullets and vision of the simulations hit it), lower eyes, less spread, slower
    void UpdateCrouch(bool want)
    {
        crouchAmount = Mathf.MoveTowards(crouchAmount, want ? 1f : 0f, crouchChangeSpeed * Time.fixedDeltaTime);
        ApplyCrouch();
    }

    void ApplyCrouch()
    {
        if (Mathf.Approximately(appliedCrouch, crouchAmount)) return;
        appliedCrouch = crouchAmount;

        // the bottom of the capsule stays where it is, the top goes down
        if (capsule != null)
        {
            float h = Mathf.Lerp(standHeight, crouchHeight, crouchAmount);
            capsule.height = h;
            capsule.center = standCenter + Vector3.up * ((h - standHeight) * 0.5f);
        }
        if (head != null) head.localPosition = standHead - Vector3.up * (crouchEyeDrop * crouchAmount);
    }

    // the eyes at full crouch
    Vector3 CrouchEye()
    {
        return transform.TransformPoint(standHead - Vector3.up * crouchEyeDrop);
    }

    // the point of the enemy is seen from the crouched eyes too
    bool SeesFromCrouch(Vector3 point)
    {
        Vector3 from = CrouchEye();
        Vector3 dir = point - from;
        float d = dir.magnitude;
        if (d < 0.01f) return true;
        return FirstHit(from, dir / d, d + 0.5f, out RaycastHit hit) && hit.collider.GetComponentInParent<Health>() == target;
    }

    // a box, a wall or a ledge between the crouched bot and where the enemy was, close to the bot
    bool HasCover()
    {
        Vector3 from = CrouchEye();
        Vector3 to = new Vector3(lastKnown.x, from.y, lastKnown.z);
        Vector3 dir = to - from;
        float d = dir.magnitude;
        if (d < 0.5f) return false;
        return FirstHit(from, dir / d, Mathf.Min(coverDistance, d), out RaycastHit hit)
               && hit.collider.GetComponentInParent<Health>() == null;
    }

    // the nearest thing on the ray that is not this bot (its capsule or the hitboxes of its model)
    bool FirstHit(Vector3 from, Vector3 dir, float length, out RaycastHit hit)
    {
        int n = Physics.RaycastNonAlloc(from, dir, rayHits, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.UseGlobal);
        // a full buffer may not hold the nearest hit
        if (n >= rayHits.Length) return Physics.Raycast(from, dir, out hit, length);

        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            if (rayHits[i].distance >= bestDistance || rayHits[i].collider.transform.IsChildOf(transform)) continue;
            bestDistance = rayHits[i].distance;
            best = i;
        }
        hit = best >= 0 ? rayHits[best] : default;
        return best >= 0;
    }

    // like the player with Shift: a bot that goes to check where it last saw or heard an enemy
    // (close to that place, or just alerted) may sneak there at half speed and without footsteps.
    // It decides once per approach; seeing an enemy or forgetting him ends the sneak.
    void UpdateSneak(bool checking, bool alerted)
    {
        bool near = (lastKnown - transform.position).sqrMagnitude < sneakDistance * sneakDistance;
        if (!checking || !(near || alerted))
        {
            IsSneaking = false;
            sneakDecided = false;
            return;
        }

        if (sneakDecided) return;
        sneakDecided = true;
        IsSneaking = Random.value < sneakChance;
    }

    // a bot does not hop all the time: only on some long straight runs, a few jumps, then a pause
    bool WantHop(bool longRun)
    {
        if (bhop == null || !bhop.Enabled) return false;

        if (!longRun)
        {
            runDecided = false;
            if (hopsLeft > 0) EndChain();
            return false;
        }

        if (hopsLeft > 0) return true;
        if (runDecided || Time.time < nextChainAt) return false;

        runDecided = true;
        if (Random.value < hopChance)
            hopsLeft = Random.Range(hopsPerChain.x, hopsPerChain.y + 1);
        return hopsLeft > 0;
    }

    void EndChain()
    {
        hopsLeft = 0;
        nextChainAt = Time.time + Random.Range(hopCooldown.x, hopCooldown.y);
    }

    // a bot with the bhop upgrade hops while it walks to somewhere far away
    void UpdateBhop(bool wantHop)
    {
        bool enabled = bhop != null && bhop.Enabled;

        if (!enabled)
        {
            hopTime = -1f;
            agent.baseOffset = baseOffset;
        }
        else if (hopTime < 0f)
        {
            if (wantHop)
            {
                hopTime = 0f;
                bhop.Jumped();
                hopsLeft--;
                if (hopsLeft <= 0) EndChain();
            }
        }
        else
        {
            hopTime += Time.fixedDeltaTime;
            float v = Mathf.Sqrt(2f * hopGravity * hopHeight);
            float h = v * hopTime - 0.5f * hopGravity * hopTime * hopTime;

            if (h <= 0f)
            {
                h = 0f;
                hopTime = -1f;
            }

            agent.baseOffset = baseOffset + h;
        }

        if (bhop != null) bhop.Tick(hopTime < 0f, agent.velocity.magnitude > 0.5f);

        bool inAir = hopTime >= 0f;
        weapon.ExternalInAir = inAir;
        weapon.ExternalSpreadMultiplier = inAir ? airSpreadMultiplier : Mathf.Lerp(1f, crouchSpread, crouchAmount);
    }

    void Wander()
    {
        if (agent.pathPending) return;

        // a bigger map tells the wander area through MapBounds, the arena keeps arenaHalfSize
        float hx = bounds != null && bounds.wanderHalfX > 0f ? bounds.wanderHalfX : arenaHalfSize;
        float hz = bounds != null && bounds.wanderHalfZ > 0f ? bounds.wanderHalfZ : arenaHalfSize;
        var round = RoundManager.Current;
        bool searchMid = (hx > arenaHalfSize || hz > arenaHalfSize) && round != null
                         && !round.IsSolo && !round.InBreak && !round.MatchOver
                         && round.TimeLeft <= Mathf.Min(30f, round.roundTime * 0.5f);
        if (bounds != null && bounds.patrolPoints != null && bounds.patrolPoints.Length > 0)
        {
            // The authored destinations are connected streets, including the correct floor in stacked passages.
            Vector3 rally = bounds.rallyPoint;
            if (agent.hasPath && agent.remainingDistance > 1.5f &&
                (!searchMid || (agent.destination - rally).sqrMagnitude < 36f)) return;

            Vector3 goal = bounds.patrolPoints[Random.Range(0, bounds.patrolPoints.Length)];
            if (searchMid)
            {
                Vector2 offset = Random.insideUnitCircle * 3f;
                goal = rally + new Vector3(offset.x, 0f, offset.y);
            }
            if (NavMesh.SamplePosition(goal, out NavMeshHit waypoint, 1.5f, NavMesh.AllAreas))
                agent.SetDestination(waypoint.position);
            return;
        }
        if (searchMid)
        {
            // Search the same central lanes from either side when time is short and contact has been lost.
            hx = Mathf.Min(hx, 10f);
            hz = Mathf.Min(hz, 10f);
        }

        const float sampleRadius = 6f;
        if (agent.hasPath && agent.remainingDistance > 1.5f)
        {
            if (!searchMid) return;
            Vector3 goal = agent.destination;
            // A point sampled beside cover may lie just outside the patrol rectangle.
            if (Mathf.Abs(goal.x) <= hx + sampleRadius && Mathf.Abs(goal.z) <= hz + sampleRadius) return;
        }
        Vector3 point = new Vector3(
            Random.Range(-hx, hx), 0f,
            Random.Range(-hz, hz));

        if (NavMesh.SamplePosition(point, out NavMeshHit hit, sampleRadius, NavMesh.AllAreas))
            agent.SetDestination(hit.position);
    }

    void Face(Vector3 direction)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.01f) return;

        Quaternion look = Quaternion.LookRotation(direction);
        transform.rotation = Quaternion.Slerp(transform.rotation, look, turnSpeed * Time.fixedDeltaTime);
    }

    // the enemy the bot sees; enemyPoint gets the point of his body the bot sees
    Health FindVisibleEnemy()
    {
        // (the team is checked again: after the halftime side switch an old target can be a teammate now)
        if (target != null && target.team != self.team && !target.IsDead && target.gameObject.activeInHierarchy &&
            CanSee(target, out enemyPoint))
            return target;

        Health best = null;
        float bestDistance = float.MaxValue;
        Vector3 bestPoint = Vector3.zero;

        foreach (Health h in Health.All)
        {
            if (h == self || h.team == self.team || h.IsDead || !h.gameObject.activeInHierarchy) continue;

            float d = (h.transform.position - transform.position).sqrMagnitude;
            if (d < bestDistance && CanSee(h, out Vector3 point))
            {
                bestDistance = d;
                best = h;
                bestPoint = point;
            }
        }
        enemyPoint = bestPoint;
        return best;
    }

    bool CanSee(Health t, out Vector3 point)
    {
        Vector3 from = head.position;
        Collider c = t.GetComponent<Collider>();
        Bounds b = c != null ? c.bounds : new Bounds(t.transform.position, Vector3.zero);

        // distance and field of view are measured to the centre of the body, as before
        point = b.center;
        Vector3 toTarget = b.center - from;
        float distance = toTarget.magnitude;
        if (distance < 0.01f) return true;

        float blind = GrenadeSystem.GetBlindness(self);
        if (blind > 0.55f && distance > 2f) return false;

        float radius = viewRange * Mathf.Lerp(1f, 0.2f, blind);
        Stealth st = t.GetComponent<Stealth>();
        if (st != null) radius = st.DetectionRadius(radius);
        if (distance > radius) return false;

        Vector3 flat = toTarget;
        flat.y = 0f;
        bool alerted = Time.time < alertUntil;
        if (!alerted && flat.sqrMagnitude > 0.0001f && Vector3.Angle(transform.forward, flat) > fieldOfView * 0.5f)
            return false;

        // the target is seen if any point of his body has a clear line, the first clear point is where the bot aims.
        // A character model has hitboxes (CharacterBody): their centres, so the line goes to a real part of the body.
        CharacterBody cb = t.GetComponent<CharacterBody>();
        int count = cb != null ? cb.AimPoints(bodyPoints) : 0;
        if (count == 0) count = BodyPoints(b, flat);
        for (int i = 0; i < count; i++)
        {
            if (GrenadeSystem.IsLineBlockedBySmoke(from, bodyPoints[i])) continue;
            Vector3 dir = bodyPoints[i] - from;
            float d = dir.magnitude;
            if (d < 0.01f) continue;

            // the bot's own model is not in the way (its eyes can be outside its head hitbox)
            if (FirstHit(from, dir / d, radius, out RaycastHit hit) && hit.collider.GetComponentInParent<Health>() == t)
            {
                point = bodyPoints[i];
                return true;
            }
        }
        return false;
    }

    // points of the capsule to look at, best for aiming first: centre, upper chest, head, then the shoulders.
    // They come from the collider, so a crouching player (smaller collider) has them lower.
    int BodyPoints(Bounds b, Vector3 flatToTarget)
    {
        Vector3 centre = b.center;
        Vector3 headPoint = new Vector3(centre.x, Mathf.Max(centre.y, b.max.y - HeadBelowTop), centre.z);
        Vector3 chest = Vector3.Lerp(centre, headPoint, 0.5f);

        bodyPoints[0] = centre;
        bodyPoints[1] = chest;
        bodyPoints[2] = headPoint;
        if (flatToTarget.sqrMagnitude < 0.0001f) return 3;

        // to the left and right of the line of sight, still inside the body
        float sideways = Mathf.Min(ShoulderOffset, Mathf.Min(b.extents.x, b.extents.z) * 0.8f);
        Vector3 side = Vector3.Cross(Vector3.up, flatToTarget).normalized * sideways;
        bodyPoints[3] = chest + side;
        bodyPoints[4] = chest - side;
        return 5;
    }
}
