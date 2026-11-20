#include "postgres.h"

#include "catalog/pg_type_d.h"
#include "common/hashfn.h"
#include "common/int.h"
#include "executor/spi.h"
#include "fmgr.h"
#include "lib/stringinfo.h"
#include "miscadmin.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/jsonb.h"
#include "utils/memutils.h"

PG_MODULE_MAGIC;
PG_FUNCTION_INFO_V1(bluetusk_graph_walk);

typedef enum
{
    GRAPH_NEIGHBORS,
    GRAPH_REACHABLE,
    GRAPH_SHORTEST,
    GRAPH_WEIGHTED
} GraphOperation;

typedef struct GraphVertex
{
    char *key;
    char *edge;
    struct GraphVertex *parent;
    struct GraphVertex *anchor;
    int depth;
    int64 cost;
} GraphVertex;

typedef struct
{
    const char *key;
    GraphVertex *vertex;
} GraphVisit;

typedef struct
{
    MemoryContext memory;
    HTAB *visited;
    GraphVertex **queue;
    int count;
    int capacity;
    int seed_count;
    int edges;
    int peak_frontier;
    int max_depth;
    int max_frontier;
    int max_edges;
    int max_results;
    int max_bytes;
    Jsonb *excluded;
    const char *exhausted;
} GraphWalk;

static uint32
hash_vertex(const void *key, Size key_size)
{
    const char *value = *(const char *const *) key;

    return hash_bytes((const unsigned char *) value, strlen(value));
}

static int
compare_vertex(const void *left, const void *right, Size key_size)
{
    return strcmp(*(const char *const *) left, *(const char *const *) right);
}

static bool
has_memory(GraphWalk *walk)
{
    if (MemoryContextMemAllocated(walk->memory, true) > (Size) walk->max_bytes)
    {
        walk->exhausted = "memory";
        return false;
    }
    return true;
}

/* Both candidates are at the same depth, so compare their keys from the root. */
static int
compare_path(const GraphVertex *left, const GraphVertex *right)
{
    int result;

    if (left == right || left->parent == NULL)
        return 0;
    result = compare_path(left->parent, right->parent);
    return result == 0 ? strcmp(left->edge, right->edge) : result;
}

static GraphVertex *
add_vertex(GraphWalk *walk, const char *key, const char *edge, GraphVertex *parent,
           int64 cost, bool weighted)
{
    GraphVisit *visit;
    GraphVertex *vertex;
    bool found;

    visit = hash_search(walk->visited, &key, HASH_ENTER, &found);
    if (found)
    {
        int parent_order;

        vertex = visit->vertex;
        if (!weighted || cost > vertex->cost)
            return NULL;
        if (cost == vertex->cost)
        {
            parent_order = compare_path(parent, vertex->parent);
            if (parent_order > 0 || (parent_order == 0 && strcmp(edge, vertex->edge) >= 0))
                return NULL;
        }
        vertex->cost = cost;
        vertex->parent = parent;
        vertex->anchor = parent->anchor;
        pfree(vertex->edge);
        vertex->edge = pstrdup(edge);
        return vertex;
    }

    vertex = palloc(sizeof(*vertex));
    vertex->key = pstrdup(key);
    vertex->edge = edge == NULL ? NULL : pstrdup(edge);
    vertex->parent = parent;
    vertex->anchor = parent == NULL ? vertex : parent->anchor;
    vertex->depth = parent == NULL ? 0 : parent->depth + 1;
    vertex->cost = cost;
    visit->key = vertex->key;
    visit->vertex = vertex;

    if (walk->count == walk->capacity)
    {
        walk->capacity *= 2;
        walk->queue = repalloc(walk->queue, sizeof(*walk->queue) * walk->capacity);
    }
    walk->queue[walk->count++] = vertex;
    return vertex;
}

static void
check_spi(int result, const char *operation)
{
    if (result < 0)
        elog(ERROR, "BlueTusk Graph SPI %s failed: %s", operation, SPI_result_code_string(result));
}

static SPIPlanPtr
prepare_plan(const char *sql, int nargs, Oid *types)
{
    MemoryContext memory = CurrentMemoryContext;
    SPIPlanPtr plan = SPI_prepare(sql, nargs, types);

    /* SPI operations finish in the SPI procedure context. Walk state must
     * live outside it so we own its lifetime and can account for its bytes. */
    MemoryContextSwitchTo(memory);
    if (plan == NULL)
        check_spi(SPI_result, "prepare");
    return plan;
}

static char *
find_visible_vertex(const char *sql, Datum key)
{
    MemoryContext memory = CurrentMemoryContext;
    Oid types[] = {JSONBOID};
    SPIPlanPtr plan = prepare_plan(sql, 1, types);
    char *vertex = NULL;

    check_spi(SPI_execute_plan(plan, &key, NULL, true, 1), "anchor");
    MemoryContextSwitchTo(memory);
    if (SPI_processed == 1)
        vertex = SPI_getvalue(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1);
    SPI_freetuptable(SPI_tuptable);
    SPI_freeplan(plan);
    return vertex;
}

static Portal
open_frontier(SPIPlanPtr plan, StringInfo frontier, int maximum, const char *after,
              Jsonb *excluded)
{
    MemoryContext memory = CurrentMemoryContext;
    Datum args[4];
    char nulls[] = {' ', ' ', 'n', 'n'};
    Portal portal;

    args[0] = DirectFunctionCall1(jsonb_in, CStringGetDatum(frontier->data));
    args[1] = Int32GetDatum(maximum);
    args[2] = (Datum) 0;
    args[3] = (Datum) 0;
    if (after != NULL)
    {
        args[2] = CStringGetTextDatum(after);
        nulls[2] = ' ';
    }
    if (excluded != NULL)
    {
        args[3] = PointerGetDatum(excluded);
        nulls[3] = ' ';
    }
    portal = SPI_cursor_open(NULL, plan, args, nulls, true);
    MemoryContextSwitchTo(memory);
    if (portal == NULL)
        check_spi(SPI_result, "open frontier");
    pfree(DatumGetPointer(args[0]));
    if (after != NULL)
        pfree(DatumGetPointer(args[2]));
    return portal;
}

static void
read_neighbors(GraphWalk *walk, SPIPlanPtr plan, const char *start,
               const char *after, StringInfo rows, StringInfo continuation)
{
    StringInfoData frontier;
    Portal portal;
    int total = 0;
    int maximum = Min(walk->max_results, walk->max_edges);
    char *previous = NULL;
    bool done = false;

    initStringInfo(&frontier);
    appendStringInfo(&frontier, "[%s]", start);
    portal = open_frontier(plan, &frontier, maximum + 1, after, walk->excluded);
    pfree(frontier.data);

    while (!done)
    {
        SPITupleTable *table;

        CHECK_FOR_INTERRUPTS();
        SPI_cursor_fetch(portal, true, Min(128, maximum - total + 1));
        MemoryContextSwitchTo(walk->memory);
        table = SPI_tuptable;
        for (uint64 i = 0; i < SPI_processed; i++)
        {
            char *edge;
            char *vertex;

            if (total == maximum)
            {
                if (total >= walk->max_edges)
                    walk->exhausted = "edges";
                else
                    appendStringInfoString(continuation, previous);
                done = true;
                break;
            }

            edge = SPI_getvalue(table->vals[i], table->tupdesc, 2);
            vertex = SPI_getvalue(table->vals[i], table->tupdesc, 3);
            appendStringInfo(rows, "%s{\"edge\":%s,\"vertex\":%s}", total == 0 ? "" : ",", edge, vertex);
            if (previous != NULL)
                pfree(previous);
            previous = edge;
            pfree(vertex);
            total++;
            walk->edges++;
            if (!has_memory(walk))
            {
                done = true;
                break;
            }
        }

        done |= SPI_processed == 0;
        SPI_freetuptable(table);
    }

    SPI_cursor_close(portal);
    if (previous != NULL)
        pfree(previous);
}

static HTAB *
make_visited(MemoryContext context)
{
    HASHCTL hash = {0};

    hash.keysize = sizeof(char *);
    hash.entrysize = sizeof(GraphVisit);
    hash.hcxt = context;
    hash.hash = hash_vertex;
    hash.match = compare_vertex;
    return hash_create("BlueTusk graph visited", 64, &hash,
                       HASH_ELEM | HASH_CONTEXT | HASH_FUNCTION | HASH_COMPARE);
}

static GraphVertex *
search_graph(GraphWalk *walk, SPIPlanPtr plan, char *const *starts, int start_count,
             const char *target, GraphOperation operation)
{
    StringInfoData frontier;
    int first = 0;
    int last;
    bool weighted = operation == GRAPH_WEIGHTED;
    GraphVertex *best = NULL;

    walk->visited = make_visited(walk->memory);
    walk->capacity = 64;
    walk->queue = palloc(sizeof(*walk->queue) * walk->capacity);
    for (int i = 0; i < start_count; i++)
        add_vertex(walk, starts[i], NULL, NULL, 0, false);
    walk->seed_count = walk->count;
    last = walk->count;
    walk->peak_frontier = last;
    if (last > walk->max_frontier)
    {
        walk->exhausted = "frontier";
        return NULL;
    }
    if (!has_memory(walk))
        return NULL;
    initStringInfo(&frontier);

    for (int depth = 0;
         depth < walk->max_depth && first < last && walk->exhausted == NULL;
         depth++)
    {
        Portal portal;
        bool done = false;
        GraphVertex *found = NULL;

        CHECK_FOR_INTERRUPTS();
        if (weighted)
        {
            /* A vertex reached in fewer hops may still beat a cheaper path
             * that leaves too little depth to finish. Deduplicate per layer. */
            hash_destroy(walk->visited);
            walk->visited = make_visited(walk->memory);
        }
        walk->peak_frontier = Max(walk->peak_frontier, last - first);
        resetStringInfo(&frontier);
        appendStringInfoChar(&frontier, '[');
        for (int i = first; i < last; i++)
            appendStringInfo(&frontier, "%s%s", i == first ? "" : ",", walk->queue[i]->key);
        appendStringInfoChar(&frontier, ']');
        if (!has_memory(walk))
            break;
        portal = open_frontier(plan, &frontier, walk->max_edges - walk->edges + 1,
                               NULL, walk->excluded);

        while (!done)
        {
            SPITupleTable *table;

            CHECK_FOR_INTERRUPTS();
            SPI_cursor_fetch(portal, true, Min(128, walk->max_edges - walk->edges + 1));
            MemoryContextSwitchTo(walk->memory);
            table = SPI_tuptable;
            for (uint64 i = 0; i < SPI_processed; i++)
            {
                HeapTuple tuple = table->vals[i];
                TupleDesc descriptor = table->tupdesc;
                bool is_null;
                int64 ordinal;
                char *edge;
                char *key;
                GraphVertex *vertex;
                GraphVertex *parent;
                int64 cost = 0;

                if (walk->edges == walk->max_edges)
                {
                    walk->exhausted = "edges";
                    break;
                }
                walk->edges++;
                ordinal = DatumGetInt64(SPI_getbinval(tuple, descriptor, 1, &is_null));
                if (is_null || ordinal < 1 || ordinal > last - first)
                    elog(ERROR, "Invalid BlueTusk Graph frontier ordinal");

                parent = walk->queue[first + (int) ordinal - 1];
                if (weighted)
                {
                    int64 weight = DatumGetInt64(SPI_getbinval(tuple, descriptor, 4, &is_null));

                    if (is_null || weight < 0)
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                                        errmsg("Graph edge weights must be non-null and nonnegative")));
                    if (pg_add_s64_overflow(parent->cost, weight, &cost))
                        ereport(ERROR, (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                                        errmsg("Graph path weight exceeds bigint")));
                }
                key = SPI_getvalue(tuple, descriptor, 3);
                edge = SPI_getvalue(tuple, descriptor, 2);
                vertex = add_vertex(walk, key, edge, parent, cost, weighted);
                pfree(key);
                pfree(edge);
                if (vertex == NULL)
                    continue;

                if (!has_memory(walk))
                    break;
                if (target != NULL && strcmp(vertex->key, target) == 0)
                {
                    found = vertex;
                    if (!weighted)
                        break;
                }
                if (operation == GRAPH_REACHABLE && walk->count - walk->seed_count > walk->max_results)
                {
                    walk->exhausted = "results";
                    break;
                }
                if (vertex->depth < walk->max_depth && walk->count - last > walk->max_frontier)
                {
                    walk->exhausted = "frontier";
                    break;
                }
            }
            done = SPI_processed == 0 || (!weighted && found != NULL) || walk->exhausted != NULL;
            SPI_freetuptable(table);
        }
        SPI_cursor_close(portal);
        if (found != NULL && !weighted)
        {
            pfree(frontier.data);
            return found;
        }
        if (found != NULL && (best == NULL || found->cost < best->cost))
            best = found;
        first = last;
        last = walk->count;
    }

    pfree(frontier.data);
    return best;
}

static void
append_path(StringInfo result, GraphVertex *last, bool weighted)
{
    GraphVertex *path[65];
    int depth = last->depth;

    for (GraphVertex *vertex = last; vertex != NULL; vertex = vertex->parent)
        path[vertex->depth] = vertex;

    appendStringInfoString(result, "{\"vertices\":[");
    for (int i = 0; i <= depth; i++)
        appendStringInfo(result, "%s%s", i == 0 ? "" : ",", path[i]->key);
    appendStringInfoString(result, "],\"edges\":[");
    for (int i = 1; i <= depth; i++)
        appendStringInfo(result, "%s%s", i == 1 ? "" : ",", path[i]->edge);
    if (weighted)
        appendStringInfo(result, "],\"cost\":" INT64_FORMAT "}", last->cost);
    else
        appendStringInfoString(result, "]}");
}

static int
get_limit(FunctionCallInfo fcinfo, int index, int minimum, int maximum, const char *name)
{
    int value = PG_GETARG_INT32(index);

    if (value < minimum || value > maximum)
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                        errmsg("%s must be between %d and %d", name, minimum, maximum)));
    return value;
}

static void
check_key(Jsonb *key)
{
    if (!JB_ROOT_IS_ARRAY(key) || JB_ROOT_IS_SCALAR(key) ||
        JB_ROOT_COUNT(key) < 1 || JB_ROOT_COUNT(key) > 8 || VARSIZE(key) > 4096)
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                        errmsg("Graph vertex keys must be arrays of one to eight key values")));
}

Datum
bluetusk_graph_walk(PG_FUNCTION_ARGS)
{
    MemoryContext work;
    MemoryContext parent = CurrentMemoryContext;
    GraphWalk walk = {0};
    GraphOperation operation;
    const char *operation_name;
    const char *direction;
    const char *after = NULL;
    SPIPlanPtr step;
    Datum plan_args[7];
    char plan_nulls[] = {' ', ' ', ' ', 'n', ' ', 'n', 'n'};
    const bool extended = PG_NARGS() == 18;
    const bool multiple = extended && !PG_ARGISNULL(17);
    char *anchor_sql;
    char *target_sql;
    char *step_sql;
    char *starts[128];
    int start_count = 0;
    char *target = NULL;
    GraphVertex *last = NULL;
    StringInfoData rows;
    StringInfoData continuation;
    StringInfoData result;
    Datum json;

    for (int i = 0; i <= 12; i++)
        if (i != 6 && i != 7 && !(multiple && (i == 2 || i == 3)) && PG_ARGISNULL(i))
            ereport(ERROR, (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                            errmsg("Graph traversal requires all non-target arguments")));

    operation_name = text_to_cstring(PG_GETARG_TEXT_PP(5));
    if (strcmp(operation_name, "neighbors") == 0)
        operation = GRAPH_NEIGHBORS;
    else if (strcmp(operation_name, "reachable") == 0)
        operation = GRAPH_REACHABLE;
    else if (strcmp(operation_name, "shortest_path") == 0)
        operation = GRAPH_SHORTEST;
    else if (strcmp(operation_name, "weighted_shortest_path") == 0)
        operation = GRAPH_WEIGHTED;
    else
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("Unknown graph operation")));

    direction = text_to_cstring(PG_GETARG_TEXT_PP(4));
    if (strcmp(direction, "outgoing") != 0 && strcmp(direction, "incoming") != 0 &&
        strcmp(direction, "undirected") != 0)
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("Unknown graph direction")));

    if (multiple)
    {
        Jsonb *anchors = PG_GETARG_JSONB_P(17);

        if (operation != GRAPH_REACHABLE || !JB_ROOT_IS_ARRAY(anchors) ||
            JB_ROOT_IS_SCALAR(anchors) || JB_ROOT_COUNT(anchors) < 1 ||
            JB_ROOT_COUNT(anchors) > lengthof(starts) || VARSIZE(anchors) > 65536)
            ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                            errmsg("Reachability requires one to 128 bounded anchor identities")));
    }
    else
        check_key(PG_GETARG_JSONB_P(3));
    if (operation == GRAPH_SHORTEST || operation == GRAPH_WEIGHTED)
    {
        if (PG_ARGISNULL(6) || PG_ARGISNULL(7))
            ereport(ERROR, (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                            errmsg("Shortest path requires a target vertex")));
        check_key(PG_GETARG_JSONB_P(7));
    }
    if (operation == GRAPH_WEIGHTED && PG_ARGISNULL(15))
        ereport(ERROR, (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                        errmsg("Weighted shortest path requires a weight property")));
    walk.max_depth = get_limit(fcinfo, 8, 0, 64, "max_depth");
    walk.max_frontier = get_limit(fcinfo, 9, 1, 100000, "max_frontier");
    walk.max_edges = get_limit(fcinfo, 10, 1, 1000000, "max_edges");
    walk.max_results = get_limit(fcinfo, 11, 1, 10000, "max_results");
    walk.max_bytes = get_limit(fcinfo, 12, 65536, 67108864, "max_bytes");

    work = AllocSetContextCreate(parent, "BlueTusk graph traversal", ALLOCSET_SMALL_SIZES);
    walk.memory = work;
    MemoryContextSwitchTo(work);
    if (!PG_ARGISNULL(14))
    {
        walk.excluded = PG_GETARG_JSONB_P(14);
        if (!JB_ROOT_IS_ARRAY(walk.excluded) || JB_ROOT_IS_SCALAR(walk.excluded) ||
            JB_ROOT_COUNT(walk.excluded) > 128 || VARSIZE(walk.excluded) > 32768)
            ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                            errmsg("A graph scenario can exclude up to 128 edge identities")));
    }
    if (!PG_ARGISNULL(13))
    {
        Jsonb *cursor = PG_GETARG_JSONB_P(13);

        if (operation != GRAPH_NEIGHBORS || !JB_ROOT_IS_ARRAY(cursor) ||
            JB_ROOT_COUNT(cursor) != 2 || VARSIZE(cursor) > 4096)
            ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("Invalid graph page cursor")));
        after = JsonbToCString(NULL, &cursor->root, VARSIZE(cursor));
    }
    check_spi(SPI_connect(), "connect");
    MemoryContextSwitchTo(work);
    initStringInfo(&rows);
    initStringInfo(&continuation);
    plan_args[0] = PG_GETARG_DATUM(0);
    plan_args[1] = PG_GETARG_DATUM(1);
    plan_args[2] = (Datum) 0;
    if (multiple)
        plan_nulls[2] = 'n';
    else
        plan_args[2] = PG_GETARG_DATUM(2);
    plan_args[3] = (Datum) 0;
    plan_args[4] = PG_GETARG_DATUM(4);
    plan_args[5] = (Datum) 0;
    if (operation == GRAPH_SHORTEST || operation == GRAPH_WEIGHTED)
    {
        plan_args[3] = PG_GETARG_DATUM(6);
        plan_nulls[3] = ' ';
    }
    if (operation == GRAPH_WEIGHTED)
    {
        plan_args[5] = PG_GETARG_DATUM(15);
        plan_nulls[5] = ' ';
    }
    plan_args[6] = (Datum) 0;
    if (extended && !PG_ARGISNULL(16))
    {
        plan_args[6] = PG_GETARG_DATUM(16);
        plan_nulls[6] = ' ';
    }
    check_spi(SPI_execute_with_args(
        extended
            ? "SELECT * FROM bluetusk_graph._plan_v2($1, $2, $3, $4, $5, $6, $7)"
            : "SELECT * FROM bluetusk_graph._plan($1, $2, $3, $4, $5, $6)",
        extended ? 7 : 6,
        (Oid[]) {TEXTOID, TEXTOID, TEXTOID, TEXTOID, TEXTOID, TEXTOID, TEXTARRAYOID},
        plan_args, plan_nulls, true, 1), "resolve graph");
    MemoryContextSwitchTo(work);
    if (SPI_processed != 1)
        elog(ERROR, "BlueTusk Graph could not resolve its registered plan");

    anchor_sql = SPI_getvalue(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1);
    target_sql = SPI_getvalue(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 2);
    step_sql = SPI_getvalue(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 3);
    if (multiple)
    {
        Datum definition;
        bool missing_definition;

        definition = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 4, &missing_definition);
        if (missing_definition)
            elog(ERROR, "BlueTusk Graph plan is missing its vertex mappings");
        definition = PointerGetDatum(DatumGetJsonbPCopy(definition));
        SPI_freetuptable(SPI_tuptable);
        check_spi(SPI_execute_with_args(
            "SELECT * FROM bluetusk_graph._visible_anchors($1, $2)", 2,
            (Oid[]) {JSONBOID, JSONBOID}, (Datum[]) {definition, PG_GETARG_DATUM(17)},
            NULL, true, lengthof(starts) + 1), "resolve anchors");
        MemoryContextSwitchTo(work);
        if (SPI_processed > lengthof(starts))
            elog(ERROR, "BlueTusk Graph returned too many anchors");
        for (uint64 i = 0; i < SPI_processed; i++)
            starts[start_count++] = SPI_getvalue(SPI_tuptable->vals[i], SPI_tuptable->tupdesc, 1);
        SPI_freetuptable(SPI_tuptable);
    }
    else
    {
        SPI_freetuptable(SPI_tuptable);
        starts[0] = find_visible_vertex(anchor_sql, PG_GETARG_DATUM(3));
        start_count = starts[0] == NULL ? 0 : 1;
    }

    if ((operation == GRAPH_SHORTEST || operation == GRAPH_WEIGHTED) && start_count > 0)
        target = find_visible_vertex(target_sql, PG_GETARG_DATUM(7));
    if (start_count > 0 && target != NULL && strcmp(starts[0], target) == 0)
    {
        last = palloc0(sizeof(*last));
        last->key = starts[0];
    }
    else if (start_count > 0 && step_sql != NULL && walk.max_depth > 0 &&
             ((operation != GRAPH_SHORTEST && operation != GRAPH_WEIGHTED) || target != NULL) &&
             has_memory(&walk))
    {
        step = prepare_plan(step_sql, 4, (Oid[]) {JSONBOID, INT4OID, TEXTOID, JSONBOID});
        if (operation == GRAPH_NEIGHBORS)
            read_neighbors(&walk, step, starts[0], after, &rows, &continuation);
        else
            last = search_graph(&walk, step, starts, start_count, target, operation);
        SPI_freeplan(step);
    }

    if (operation == GRAPH_REACHABLE && walk.exhausted == NULL)
    {
        for (int i = walk.seed_count; i < walk.count; i++)
        {
            GraphVertex *vertex = walk.queue[i];

            appendStringInfo(&rows, "%s{\"vertex\":%s,\"hops\":%d",
                             i == walk.seed_count ? "" : ",", vertex->key, vertex->depth);
            if (multiple)
                appendStringInfo(&rows, ",\"anchor\":%s", vertex->anchor->key);
            appendStringInfoChar(&rows, '}');
            if (!has_memory(&walk))
                break;
        }
    }
    if (walk.exhausted != NULL)
    {
        resetStringInfo(&rows);
        resetStringInfo(&continuation);
        last = NULL;
    }

    initStringInfo(&result);
    if (walk.exhausted == NULL)
        appendStringInfo(&result,
                         "{\"status\":\"complete\",\"reason\":null,\"rows\":[%s],\"next\":%s,"
                         "\"work\":{\"edges\":%d,\"frontier\":%d},\"path\":",
                         rows.data, continuation.len > 0 ? continuation.data : "null",
                         walk.edges, walk.peak_frontier);
    else
    {
        /* The reason is one of four fixed ASCII strings. */
        appendStringInfo(&result,
                         "{\"status\":\"budget_exhausted\",\"reason\":\"%s\",\"rows\":[],"
                         "\"next\":null,\"work\":{\"edges\":%d,\"frontier\":%d},\"path\":",
                         walk.exhausted, walk.edges, walk.peak_frontier);
    }
    if (last == NULL)
        appendStringInfoString(&result, "null");
    else
        append_path(&result, last, operation == GRAPH_WEIGHTED);
    appendStringInfoChar(&result, '}');

    check_spi(SPI_finish(), "finish");
    MemoryContextSwitchTo(parent);
    json = DirectFunctionCall1(jsonb_in, CStringGetDatum(result.data));
    MemoryContextDelete(work);
    return json;
}
