#include "postgres.h"
#include "fmgr.h"
#include "libpq/oauth.h"

PG_MODULE_MAGIC;

static bool
validate_token(const ValidatorModuleState *state,
               const char *token,
               const char *role,
               ValidatorModuleResult *result)
{
    (void) state;
    result->authorized = strcmp(token, "bluetusk-oauth-token") == 0;
    result->authn_id = pstrdup(role);
    return true;
}

static const OAuthValidatorCallbacks callbacks =
{
    .magic = PG_OAUTH_VALIDATOR_MAGIC,
    .startup_cb = NULL,
    .shutdown_cb = NULL,
    .validate_cb = validate_token,
};

PGDLLEXPORT const OAuthValidatorCallbacks *
_PG_oauth_validator_module_init(void)
{
    return &callbacks;
}
