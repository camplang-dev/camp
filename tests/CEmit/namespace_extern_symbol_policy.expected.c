// file: namespace_extern_symbol_policy.c
#include "namespace_extern_symbol_policy_private.h"

/* Private file declarations. */
int Posix_getpid(void);
int puts(uint8_t *text);
int chdir(uint8_t *path);

int Posix_main(void)
{
	return (Posix_getpid() + chdir(0));
}

// file: namespace_extern_symbol_policy.h
#ifndef NAMESPACE_EXTERN_SYMBOL_POLICY_H_
#define NAMESPACE_EXTERN_SYMBOL_POLICY_H_

#include "namespace_extern_symbol_policy_private.h"

int Posix_main(void);

#endif
// file: namespace_extern_symbol_policy_private.h
#ifndef NAMESPACE_EXTERN_SYMBOL_POLICY_PRIVATE_H_
#define NAMESPACE_EXTERN_SYMBOL_POLICY_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Layouts. */

/* Function declarations. */
int Posix_main(void);

/* Object declarations. */


#endif
