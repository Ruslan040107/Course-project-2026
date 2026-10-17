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
    float alertUntil;

    NavMeshAgent agent;
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
        self.Damaged += OnDamaged;
    }

    void OnDisable()
    {
        self.Damaged -= OnDamaged;
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
        float weaponFactor = weapon != null ? weapon.moveSpeedFactor : 1f;
        agent.speed = stealth != null ? baseSpeed * weaponFactor * stealth.SpeedFactor() : baseSpeed * weaponFactor;

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
            if (distance > stopDistance) agent.SetDestination(enemy.transform.position);
            else agent.ResetPath();

            if (seenTime >= reactionTime)
            {
                head.LookAt(AimPoint(enemy));
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
        }
    }

    void Wander()
    {
        if (agent.pathPending) return;
        if (agent.hasPath && agent.remainingDistance > 1.5f) return;

        Vector3 point = new Vector3(
            Random.Range(-arenaHalfSize, arenaHalfSize), 0f,
            Random.Range(-arenaHalfSize, arenaHalfSize));

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