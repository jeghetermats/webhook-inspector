// Monthly cost budget on the resource group. Alerts only notify: they do not stop
// resources and are not a hard cap (PLAN.md section 10).
param name string
param contactEmail string
param startDate string

@description('Monthly budget in the billing currency.')
param amount int = 20

resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: 'budget-${name}'
  properties: {
    category: 'Cost'
    amount: amount
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: startDate
    }
    notifications: {
      actualAbove5: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 25 // 25 % of 20 = 5
        thresholdType: 'Actual'
        contactEmails: [contactEmail]
      }
      actualAbove20: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Actual'
        contactEmails: [contactEmail]
      }
    }
  }
}
