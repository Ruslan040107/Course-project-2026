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
    float baseOffset;
    int hopsLeft;            // jumps left in the current chain
    bool runDecided;         // already decided whether to hop on this run
    float nextChainAt;

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

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        self = GetComponent<Health>();
        agent.updateRotation = false;
        baseSpeed = agent.speed;
        baseOffset = agent.baseOffset;
        bounds = FindAnyObjectByType<MapBounds>();
    }

    Vector3 AimPoint(Health t)
    {
        Collider c = t.GetComponent<Collider>();
        return c != null ? c.bounds.center : t.transform.position;
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
        agent.baseOffset = baseOffset;
        self.Damaged += OnDamaged;
    }

    void OnDisable()
    {
        self.Damaged -= OnDamaged;
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
        float bhopFactor = bhop != null ? bhop.SpeedMultiplier : 1f;
        float weaponFactor = weapon != null ? weapon.moveSpeedFactor : 1f;
        agent.speed = (stealth != null ? baseSpeed * weaponFactor * stealth.SpeedFactor() : baseSpeed * weaponFactor) * bhopFactor;

        bool wantHop = false;
        Health enemy = FindVisibleEnemy();

        if (enemy != null)
        {
            if (enemy != target) seenTime = 0f;
            target = enemy;
            lastKnown = enemy.transform.position;
            hasLastKnown = true;
            lastSeenAt = Time.time;
            seenTime += Time.fixedDeltaTime;

            Face(enemy.transform.position - transform.position);

            float distance = Vector3.Distance(transform.position, enemy.transform.position);
            // a bot with a knife walks up close, with a gun it keeps its distance
            float stop = weapon.IsMelee ? meleeStopDistance : stopDistance;
            if (distance > stop)
            {
                agent.SetDestination(enemy.transform.position);
            }
            else
            {
                agent.ResetPath();
            }

            if (seenTime >= reactionTime)
            {
                head.LookAt(AimPoint(enemy));
                weapon.Scoped = distance > noScopeDistance;
                float speedRatio = agent.velocity.magnitude / Mathf.Max(baseSpeed, 0.01f);
                weapon.ExternalMoveInaccuracy = hopTime >= 0f ? 1.5f : Mathf.Clamp01(Mathf.InverseLerp(0.25f, 0.8f, speedRatio));

                if (!weapon.IsMelee || distance <= weapon.data.meleeRange)
                    weapon.TryFire();
            }
        }
        else
        {
            target = null;
            seenTime = 0f;
            if (weapon.Ammo < weapon.magazineSize) weapon.StartReload();

            if (hasLastKnown && Time.time - lastSeenAt < memoryTime)
                agent.SetDestination(lastKnown);
            else
                Wander();

            Face(agent.desiredVelocity);

            bool alerted = Time.time < alertUntil;
            bool longRun = !alerted && agent.hasPath && !agent.pathPending
                           && agent.remainingDistance > hopMinDistance
                           && Vector3.Distance(transform.position, agent.steeringTarget) > hopMinStraight;
            wantHop = WantHop(longRun);
        }

        // in a fight the chain is broken
        if (enemy != null && hopsLeft > 0) EndChain();

        UpdateBhop(wantHop);
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
        weapon.ExternalSpreadMultiplier = inAir ? airSpreadMultiplier : 1f;
    }

    void Wander()
    {
        if (agent.pathPending) return;
        if (agent.hasPath && agent.remainingDistance > 1.5f) return;

        // a bigger map tells the wander area through MapBounds, the arena keeps arenaHalfSize
        float hx = bounds != null && bounds.wanderHalfX > 0f ? bounds.wanderHalfX : arenaHalfSize;
        float hz = bounds != null && bounds.wanderHalfZ > 0f ? bounds.wanderHalfZ : arenaHalfSize;
        Vector3 point = new Vector3(
            Random.Range(-hx, hx), 0f,
            Random.Range(-hz, hz));

        if (NavMesh.SamplePosition(point, out NavMeshHit hit, 6f, NavMesh.AllAreas))
            agent.SetDestination(hit.position);
    }

    void Face(Vector3 direction)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.01f) return;

        Quaternion look = Quaternion.LookRotation(direction);
        transform.rotation = Quaternion.Slerp(transform.rotation, look, turnSpeed * Time.fixedDeltaTime);
    }

    Health FindVisibleEnemy()
    {
        if (target != null && !target.IsDead && target.gameObject.activeInHierarchy && CanSee(target))
            return target;

        Health best = null;
        float bestDistance = float.MaxValue;

        foreach (Health h in Health.All)
        {
            if (h == self || h.team == self.team || h.IsDead || !h.gameObject.activeInHierarchy) continue;

            float d = (h.transform.position - transform.position).sqrMagnitude;
            if (d < bestDistance && CanSee(h))
            {
                bestDistance = d;
                best = h;
            }
        }
        return best;
    }

    bool CanSee(Health t)
    {
        Vector3 from = head.position;
        Vector3 toTarget = AimPoint(t) - from;
        float distance = toTarget.magnitude;
        if (distance < 0.01f) return true;

        float radius = viewRange;
        Stealth st = t.GetComponent<Stealth>();
        if (st != null) radius = st.DetectionRadius(viewRange);
        if (distance > radius) return false;

        Vector3 flat = toTarget;
        flat.y = 0f;
        bool alerted = Time.time < alertUntil;
        if (!alerted && flat.sqrMagnitude > 0.0001f && Vector3.Angle(transform.forward, flat) > fieldOfView * 0.5f)
            return false;
        if (Physics.Raycast(from, toTarget / distance, out RaycastHit hit, radius))
            return hit.collider.GetComponentInParent<Health>() == t;

        return false;
    }
}